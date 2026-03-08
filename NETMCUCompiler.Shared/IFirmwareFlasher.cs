using System;
using System.Collections.Generic;
using System.Text;

namespace NETMCUCompiler.Shared
{
    public interface IFirmwareFlasher
    {
        Task<bool> FlashAsync(string firmwarePath, uint address, string portName);
    }
}
