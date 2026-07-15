using System.Reflection;

namespace PoolAI.Contracts;

public static class ContractAssembly
{
    public static Assembly Assembly => typeof(ContractAssembly).Assembly;
}
