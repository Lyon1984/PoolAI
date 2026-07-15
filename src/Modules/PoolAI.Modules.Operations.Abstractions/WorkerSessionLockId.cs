using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PoolAI.Modules.Operations.Abstractions;

public static class WorkerSessionLockId
{
    public static long Derive(WorkerJobIdentity job)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(job.Name));
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}
