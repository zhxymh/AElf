﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Managers;
using AElf.Kernel.Storages;

namespace AElf.Kernel
{
    public class BlockChain : LightChain, IBlockChain
    {
        public BlockChain(Hash chainId, IBlockManagerBasic blockManager, ICanonicalHashStore canonicalHashStore):base(chainId, blockManager, canonicalHashStore)
        {
        }

        // TODO: Implement
        public IBlock CurrentBlock { get; }

        public async Task<bool> HasBlock(Hash blockId)
        {
            var blk = await _blockManager.GetBlockAsync(blockId);
            return blk != null;
        }

        public async Task<bool> IsOnCanonical(Hash blockId)
        {
            throw new NotImplementedException();
        }

        private async Task AddBlockAsync(IBlock block)
        {
            await _blockManager.AddBlockAsync(block);
        }
        
        public async Task AddBlocksAsync(IEnumerable<IBlock> blocks)
        {
            foreach (var block in blocks)
            {
                await AddBlockAsync(block);
            }
        }
    }
}