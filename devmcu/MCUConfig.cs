using System;
using System.MCU.Compiler;

namespace devmcu
{
    internal class MCUConfig : ConfigureEntry
    {
        public override void Apply(CompilerOptions options)
        {
            options.SetMCUType("STM32");
            options.SetMCU("cortex-m4");
            options.SetMemoryLayout(1024 * 256, 1024 * 64); // 256KB Flash, 64KB RAM
            options.SetCStandard("c11");
            options.Define("USE_HAL_DRIVER");
            options.Define("DEV_Test", "query");
            options.SetOptimization("Os");
            options.SetStartupAddress(0x08004000); // Адрес начала пользовательского кода
            //options.SetVectorTablePath("vector_table.s");
            options.Include("Drivers/STM32F4xx_HAL_Driver/Inc");
            options.AddLibrary("Drivers/STM32F4xx_HAL_Driver/Src");
            options.InstallPackage("CMSIS");

            #region Test

            #endregion
        }
    }
}
