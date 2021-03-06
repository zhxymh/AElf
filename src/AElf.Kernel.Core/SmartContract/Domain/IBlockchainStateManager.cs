using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel.Infrastructure;
using AElf.Kernel.SmartContract.Infrastructure;
using AElf.Types;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.SmartContract.Domain
{
    public interface IBlockchainStateManager
    {
        //Task<VersionedState> GetVersionedStateAsync(Hash blockHash,long blockHeight, string key);
        Task<ByteString> GetStateAsync(string key, long blockHeight, Hash blockHash);
        Task SetBlockStateSetAsync(BlockStateSet blockStateSet);
        Task MergeBlockStateAsync(ChainStateInfo chainStateInfo, Hash blockStateHash);
        Task<ChainStateInfo> GetChainStateInfoAsync();
        Task<BlockStateSet> GetBlockStateSetAsync(Hash blockHash);
        Task RemoveBlockStateSetsAsync(IList<Hash> blockStateHashes);
    }

    public class BlockchainStateManager : IBlockchainStateManager, ITransientDependency
    {
        private readonly IStateStore<VersionedState> _versionedStates;
        private readonly INotModifiedCachedStateStore<BlockStateSet> _blockStateSets;
        private readonly IStateStore<ChainStateInfo> _chainStateInfoCollection;

        private readonly int _chainId;

        public BlockchainStateManager(IStateStore<VersionedState> versionedStates,
            INotModifiedCachedStateStore<BlockStateSet> blockStateSets,
            IStateStore<ChainStateInfo> chainStateInfoCollection,
            IOptionsSnapshot<ChainOptions> options)
        {
            _versionedStates = versionedStates;
            _blockStateSets = blockStateSets;
            _chainStateInfoCollection = chainStateInfoCollection;
            _chainId = options.Value.ChainId;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="blockHeight"></param>
        /// <param name="blockHash">should already in store</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public async Task<ByteString> GetStateAsync(string key, long blockHeight, Hash blockHash)
        {
            ByteString value = null;

            //first DB read
            var bestChainState = await _versionedStates.GetAsync(key);

            if (bestChainState != null)
            {
                if (bestChainState.BlockHash == blockHash)
                {
                    value = bestChainState.Value;
                }
                else
                {
                    if (bestChainState.BlockHeight >= blockHeight)
                    {
                        //because we may clear history state
                        throw new InvalidOperationException($"cannot read history state, best chain state hash: {bestChainState.BlockHash.ToHex()}, key: {key}, block height: {blockHeight}, block hash{blockHash.ToHex()}");
                    }

                    //find value in block state set
                    var blockStateSet = await FindBlockStateSetWithKeyAsync(key, bestChainState.BlockHeight, blockHash);
                    blockStateSet?.TryGetValue(key, out value);

                    if (value == null && (blockStateSet == null || !blockStateSet.Deletes.Contains(key) || blockStateSet.BlockHeight <= bestChainState.BlockHeight))
                    {
                        //not found value in block state sets. for example, best chain is 100, blockHeight is 105,
                        //it will find 105 ~ 101 block state set. so the value could only be the best chain state value.
                        // retry versioned state in case conflict of get state during merging  
                        bestChainState = await _versionedStates.GetAsync(key);
                        value = bestChainState.Value;
                    }
                }
            }
            else
            {
                //best chain state is null, it will find value in block state set
                var blockStateSet = await FindBlockStateSetWithKeyAsync(key, 0, blockHash);
                blockStateSet?.TryGetValue(key, out value);
                
                if (value == null && blockStateSet == null)
                {
                    // retry versioned state in case conflict of get state during merging  
                    bestChainState = await _versionedStates.GetAsync(key);
                    value = bestChainState?.Value;
                }
            }

            return value;
        }

        private async Task<BlockStateSet> FindBlockStateSetWithKeyAsync(string key, long bestChainHeight, Hash blockHash)
        {
            var blockStateKey = blockHash.ToStorageKey();
            var blockStateSet = await _blockStateSets.GetAsync(blockStateKey);

            while (blockStateSet != null && blockStateSet.BlockHeight > bestChainHeight)
            {
                if (blockStateSet.TryGetValue(key, out _)) break;

                blockStateKey = blockStateSet.PreviousHash?.ToStorageKey();

                if (blockStateKey != null)
                {
                    blockStateSet = await _blockStateSets.GetAsync(blockStateKey);
                }
                else
                {
                    blockStateSet = null;
                }
            }

            return blockStateSet;
        }

        public async Task SetBlockStateSetAsync(BlockStateSet blockStateSet)
        {
            await _blockStateSets.SetAsync(GetKey(blockStateSet), blockStateSet);
        }

        public async Task MergeBlockStateAsync(ChainStateInfo chainStateInfo, Hash blockStateHash)
        {
            var blockState = await _blockStateSets.GetAsync(blockStateHash.ToStorageKey());
            if (blockState == null)
            {
                if (chainStateInfo.Status == ChainStateMergingStatus.Merged &&
                    chainStateInfo.MergingBlockHash == blockStateHash)
                {
                    chainStateInfo.Status = ChainStateMergingStatus.Common;
                    chainStateInfo.MergingBlockHash = null;

                    await _chainStateInfoCollection.SetAsync(chainStateInfo.ChainId.ToStorageKey(), chainStateInfo);
                    return;
                }

                throw new InvalidOperationException($"cannot get block state of {blockStateHash}");
            }

            if (chainStateInfo.BlockHash == null || chainStateInfo.BlockHash == blockState.PreviousHash ||
                (chainStateInfo.Status == ChainStateMergingStatus.Merged &&
                 chainStateInfo.MergingBlockHash == blockState.BlockHash))
            {
                chainStateInfo.Status = ChainStateMergingStatus.Merging;
                chainStateInfo.MergingBlockHash = blockStateHash;

                await _chainStateInfoCollection.SetAsync(chainStateInfo.ChainId.ToStorageKey(), chainStateInfo);
                var dic = blockState.Changes.Select(change => new VersionedState()
                {
                    Key = change.Key,
                    Value = change.Value,
                    BlockHash = blockState.BlockHash,
                    BlockHeight = blockState.BlockHeight,
                    //OriginBlockHash = origin.BlockHash
                }).ToDictionary(p => p.Key, p => p);

                await _versionedStates.SetAllAsync(dic);

                await _versionedStates.RemoveAllAsync(blockState.Deletes.ToList());

                chainStateInfo.Status = ChainStateMergingStatus.Merged;
                chainStateInfo.BlockHash = blockState.BlockHash;
                chainStateInfo.BlockHeight = blockState.BlockHeight;
                await _chainStateInfoCollection.SetAsync(chainStateInfo.ChainId.ToStorageKey(), chainStateInfo);

                await _blockStateSets.RemoveAsync(blockStateHash.ToStorageKey());

                chainStateInfo.Status = ChainStateMergingStatus.Common;
                chainStateInfo.MergingBlockHash = null;

                await _chainStateInfoCollection.SetAsync(chainStateInfo.ChainId.ToStorageKey(), chainStateInfo);
            }
            else
            {
                throw new InvalidOperationException(
                    "cannot merge block not linked, check new block's previous block hash ");
            }
        }

        public async Task<ChainStateInfo> GetChainStateInfoAsync()
        {
            var o = await _chainStateInfoCollection.GetAsync(_chainId.ToStorageKey());
            return o ?? new ChainStateInfo() {ChainId = _chainId};
        }

        public async Task<BlockStateSet> GetBlockStateSetAsync(Hash blockHash)
        {
            return await _blockStateSets.GetAsync(blockHash.ToStorageKey());
        }

        public async Task RemoveBlockStateSetsAsync(IList<Hash> blockStateHashes)
        {
            await _blockStateSets.RemoveAllAsync(blockStateHashes.Select(b => b.ToStorageKey()).ToList());
        }

        private string GetKey(BlockStateSet blockStateSet)
        {
            return blockStateSet.BlockHash.ToStorageKey();
        }
    }
}