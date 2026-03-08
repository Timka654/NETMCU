using System;
using System.Collections.Generic;
using System.Text;

namespace NETMCUCompiler.Shared
{
    public record FirmwareFlashArgument(string name, bool required, string description);

    public interface IFirmwareFlasher
    {
        IEnumerable<FirmwareFlashArgument> GetArguments();

        Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string,object> args);
    }
}
