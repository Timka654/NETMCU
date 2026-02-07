using System;
using System.MCU.Compiler;

namespace NETMCUCore.STM
{
    internal class MCUConfig : ConfigureEntry
    {
        public override void Apply(CompilerOptions options)
        {
            options.AddInputConfiguration("HAL_PATH", "string",
                errors: [
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidValue, "test_invalid_value"),
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidType, "test_invalid_type"),
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "For continue - please set \"HAL_PATH\" configuration value")]);

            options.Include("%#HAL_PATH#%/Inc");
            options.AddLibrary("%#HAL_PATH#%/Src");
        }
    }
}
