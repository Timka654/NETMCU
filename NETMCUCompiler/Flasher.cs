using System;
using System.Diagnostics;
using System.IO;

namespace NETMCUCompiler
{
    public enum ProgrammerType
    {
        STLinkCLI,
        STFlash,
        OpenOCD,
        STM32CubeProgrammer,
        DfuUtil
    }

    public class FirmwareFlasher
    {
        public static bool Flash(string binFilePath, uint address, ProgrammerType programmer = ProgrammerType.STFlash)
        {
            if (!File.Exists(binFilePath))
            {
                Console.WriteLine($"[Flasher] Error: Firmware file not found: {binFilePath}");
                return false;
            }

            Console.WriteLine($"[Flasher] Starting flash process using {programmer}...");

            string tool = "";
            string args = "";

            switch (programmer)
            {
                case ProgrammerType.STFlash:
                    tool = "st-flash";
                    args = $"--reset write \"{binFilePath}\" 0x{address:X8}";
                    break;

                case ProgrammerType.STM32CubeProgrammer:
                    tool = "STM32_Programmer_CLI";
                    args = $"-c port=SWD -w \"{binFilePath}\" 0x{address:X8} -v -rst";
                    break;

                case ProgrammerType.OpenOCD:
                    tool = "openocd";
                    // For f401 by default, adjusting config might be needed as target/stm32f4x.cfg
                    args = $"-f interface/stlink.cfg -f target/stm32f4x.cfg -c \"program \\\"{binFilePath}\\\" 0x{address:X8} verify reset exit\"";
                    break;

                case ProgrammerType.DfuUtil:
                    tool = "dfu-util";
                    args = $"-d 0483:df11 -a 0 -s 0x{address:X8}:leave -D \"{binFilePath}\"";
                    break;

                case ProgrammerType.STLinkCLI:
                    tool = "ST-LINK_CLI.exe";
                    args = $"-c SWD -p \"{binFilePath}\" 0x{address:X8} -V -Rst";
                    break;
            }

            Console.WriteLine($"[Flasher] Executing: {tool} {args}");

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                
                process.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[ERR] {e.Data}"); };
                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                process.WaitForExit();
                
                if (process.ExitCode == 0)
                {
                    Console.WriteLine("[Flasher] Firmware flashed successfully.");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[Flasher] Flash failed with exit code: {process.ExitCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Flasher] Failed to start programmer tool: {ex.Message}");
                Console.WriteLine("[Flasher] Make sure the tool is installed and added to your system PATH.");
                return false;
            }
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("st-flash")]
    public class STFlashWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, string portName)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, ProgrammerType.STFlash));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("openocd")]
    public class OpenOCDWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, string portName)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, ProgrammerType.OpenOCD));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("cubeprogrammer")]
    public class STM32CubeProgrammerWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, string portName)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, ProgrammerType.STM32CubeProgrammer));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("stlinkcli")]
    public class STLinkCLIWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, string portName)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, ProgrammerType.STLinkCLI));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("dfu")]
    public class DfuUtilWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, string portName)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, ProgrammerType.DfuUtil));
        }
    }
}