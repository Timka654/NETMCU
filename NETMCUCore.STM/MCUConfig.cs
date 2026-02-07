using System;
using System.MCU.Compiler;

namespace NETMCUCore.STM
{
    internal class MCUConfig : ConfigureEntry
    {
        public override void Apply(CompilerOptions options)
        {
            options.AddInputConfiguration("HAL_VERSION", "string",
                errors: [
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidValue, "test_invalid_value"),
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidType, "test_invalid_type"),
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_VERSION\" is required configuration value")]);
            options.AddInputConfiguration("HAL_CONFIGURATION_DIR_PATH", "string",
                errors: [
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidValue, "test_invalid_value"),
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidType, "test_invalid_type"),
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_CONFIGURATION_DIR_PATH\" is required configuration value")]);
            options.AddInputConfiguration("HAL_CONFIGURATION_FILE_NAME", "string",
                errors: [
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidValue, "test_invalid_value"),
                    //new InputConfigurationErrorMessage(InputConfigurationErrorMessage.InvalidType, "test_invalid_type"),
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_CONFIGURATION_FILE_NAME\" is required configuration value")]);

            options.Include("/build_libs/%#HAL_VERSION#%-hal-driver/Inc");
            options.Include("/HAL_INPUT/%#HAL_CONFIGURATION_FILE_NAME#%");
            options.AddLibrary("$(find \"/build_libs/%#HAL_VERSION#%-hal-driver\" -name \"*.c\")");

            options.RepositoryClone("https://github.com/STMicroelectronics/%#HAL_VERSION#%-hal-driver.git", "master", 1, "/build_libs/%#HAL_VERSION#%-hal-driver");

            options.MapDrive($"%#HAL_CONFIGURATION_DIR_PATH#%", "/HAL_INPUT/%#HAL_CONFIGURATION_FILE_NAME#%");
            //options.AddLibrary("%#HAL_PATH#%/Src");
        }
    }
}
