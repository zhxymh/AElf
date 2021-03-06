using AElf.Types;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.Token
{
    public class TokenSmartContractAddressNameProvider : ISmartContractAddressNameProvider, ISingletonDependency
    {
        public static readonly Hash Name = Hash.FromString("AElf.ContractNames.Token");

        public Hash ContractName => Name;
    }
}