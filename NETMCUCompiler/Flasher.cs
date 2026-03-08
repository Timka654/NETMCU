using System;
using System.Diagnostics;
using System.IO;

namespace NETMCUCompiler
{
    public class FirmwareFlasher
    {
        public static bool Flash(string binFilePath, string tool, string args, string programmer)
        {
            if (!File.Exists(binFilePath))
            {
                Console.WriteLine($"[Flasher] Error: Firmware file not found: {binFilePath}");
                return false;
            }

            Console.WriteLine($"[Flasher] Starting flash process using {programmer}...");


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
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string, object> args)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, "st-flash", $"--reset write \"{binFilePath}\" 0x{address:X8}", "STFlash"));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("openocd")]
    public class OpenOCDWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string, object> args)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, "openocd", $"-f interface/stlink.cfg -f target/stm32f4x.cfg -c \"program \\\"{binFilePath}\\\" 0x{address:X8} verify reset exit\"", "OpenOCD"));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("cubeprogrammer")]
    public class STM32CubeProgrammerWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string, object> args)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, "STM32_Programmer_CLI", $"-c port=SWD -w \"{binFilePath}\" 0x{address:X8} -v -rst", "STM32CubeProgrammer"));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("stlinkcli")]
    public class STLinkCLIWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string, object> args)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, "ST-LINK_CLI.exe", $"-c SWD -p \"{binFilePath}\" 0x{address:X8} -V -Rst", "STLinkCLI"));
        }
    }

    [NETMCUCompiler.Shared.Attributes.MCUFirmwareFlasher("dfu")]
    public class DfuUtilWrapper : NETMCUCompiler.Shared.IFirmwareFlasher
    {
        public System.Threading.Tasks.Task<bool> FlashAsync(string firmwarePath, uint address, Dictionary<string, object> args)
        {
            return System.Threading.Tasks.Task.FromResult(FirmwareFlasher.Flash(firmwarePath, address, "dfu-util", $"-d 0483:df11 -a 0 -s 0x{address:X8}:leave -D \"{binFilePath}\"", "DfuUtil"));
        }
    }
}