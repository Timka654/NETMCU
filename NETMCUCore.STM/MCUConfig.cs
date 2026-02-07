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
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_VERSION\" is required configuration value")]);
            options.AddInputConfiguration("HAL_SERIES", "string",
                errors: [
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_SERIES\" is required configuration value")]);
            options.AddInputConfiguration("HAL_CONFIGURATION_DIR_PATH", "string",
                errors: [
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_CONFIGURATION_DIR_PATH\" is required configuration value")]);
            options.AddInputConfiguration("HAL_CONFIGURATION_FILE_NAME", "string",
                errors: [
                    new InputConfigurationErrorMessage(InputConfigurationErrorMessage.RequiredValue, "\"HAL_CONFIGURATION_FILE_NAME\" is required configuration value")]);

            options.RepositoryClone("https://github.com/STMicroelectronics/cmsis_device_%#HAL_SERIES#%.git", "master", 1, "/build_libs/cmsis_%#HAL_SERIES#%");

            options.RepositoryClone("https://github.com/STMicroelectronics/cmsis_core.git", "master", 1, "/build_libs/cmsis_core");

            options.RepositoryClone("https://github.com/STMicroelectronics/%#HAL_VERSION#%-hal-driver.git", "master", 1, "/build_libs/%#HAL_VERSION#%-hal-driver");

            options.MapDrive($"%#HAL_CONFIGURATION_DIR_PATH#%", "/HAL_INPUT/%#HAL_CONFIGURATION_FILE_NAME#%");

            options.Include("/build_libs/cmsis_core/Include");
            options.Include("/build_libs/cmsis_%#HAL_SERIES#%/Include");
            options.Include("/build_libs/%#HAL_VERSION#%-hal-driver/Inc");
            options.Include("/HAL_INPUT/%#HAL_CONFIGURATION_FILE_NAME#%");
            //options.AddLibrary("$(find \"/build_libs/%#HAL_VERSION#%-hal-driver\" -name \"*.c\")");
            options.AddLibrary("$(find \"/build_libs/%#HAL_VERSION#%-hal-driver\" -name \"*.c\" ! -name \"*_template.c\")");
            options.AddLibrary("$(find \"/build_libs/cmsis_%#HAL_SERIES#%/Source/Templates\" -name \"*.c\" ! -name \"*_template.c\")");
            //options.AddLibrary("%#HAL_PATH#%/Src");
        }
    }
}
