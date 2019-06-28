using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acs8;
using AElf.Contracts.MultiToken.Messages;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract.Sdk;
using AElf.Kernel.Token;
using AElf.Types;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.SmartContract.ExecutionPluginForAcs8
{
    public class ResourceConsumptionExecutionPlugin : IExecutionPlugin, ISingletonDependency
    {
        private readonly IHostSmartContractBridgeContextService _contextService;
        private const string AcsSymbol = "acs8";

        public ResourceConsumptionExecutionPlugin(IHostSmartContractBridgeContextService contextService)
        {
            _contextService = contextService;
        }

        private static bool IsAcs5(IReadOnlyList<ServiceDescriptor> descriptors)
        {
            return descriptors.Any(service => service.File.GetIndentity() == AcsSymbol);
        }

        public async Task<IEnumerable<Transaction>> GetPreTransactionsAsync(
            IReadOnlyList<ServiceDescriptor> descriptors, ITransactionContext transactionContext)
        {
            if (!IsAcs5(descriptors))
            {
                return new List<Transaction>();
            }

            var context = _contextService.Create();
            context.TransactionContext = transactionContext;
            var selfStub = new ResourceConsumptionContractContainer.ResourceConsumptionContractStub()
            {
                __factory = new MethodStubFactory(context)
            };

            var resourceConsumptionAmount = await selfStub.GetResourceConsumptionAmount.CallAsync(new StringValue
            {
                Value = context.TransactionContext.Transaction.MethodName
            });

            // Generate token contract stub.
            var tokenContractAddress = context.GetContractAddressByName(TokenSmartContractAddressNameProvider.Name);
            if (tokenContractAddress == null)
            {
                return new List<Transaction>();
            }

            var contractAddress = transactionContext.Transaction.To;

            var tokenStub = new TokenContractContainer.TokenContractStub
            {
                __factory = new TransactionGeneratingOnlyMethodStubFactory
                {
                    Sender = transactionContext.Transaction.From,
                    ContractAddress = tokenContractAddress
                }
            };
            if (transactionContext.Transaction.To == tokenContractAddress &&
                transactionContext.Transaction.MethodName == nameof(tokenStub.CheckThreshold))
            {
                return new List<Transaction>();
            }

            var checkThresholdTransaction = (await tokenStub.CheckThreshold.SendAsync(new CheckThresholdInput
            {
                SymbolToThreshold = {resourceConsumptionAmount.SymbolToAmount}
            })).Transaction;

            return new List<Transaction>
            {
                checkThresholdTransaction
            };
        }
    }
}