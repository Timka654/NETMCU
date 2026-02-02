using System;
using System.MCU.Compiler.Attributes;

namespace System.MCU.Compiler
{
    public class CompilerOptions
    {

        [ReplaceConfigurationValue(NameArg = nameof(name), ValueArg = nameof(value))]
        public void SetConfiguration(string name, string value) { }

        [IncludeConfigurationValue(IncludeArg = nameof(path))]
        public void Include(string path) { }

        [DriveConfigurationValue(PathArg = nameof(path))]
        public void MapDrive(string path) { }

        [PackageConfigurationValue(NameArg = nameof(name))]
        public void InstallPackage(string name) { }

        [LibraryConfigurationValue(PathArg = nameof(path))]
        public void AddLibrary(string path) { }

        // Определение макросов (аналог -D в gcc)
        // Например: options.Define("USE_HAL_DRIVER");
        [DefineConfigurationValue(NameArg = nameof(macro), ValueArg = nameof(value))]
        public void Define(string macro, string value = null) { }


        [ReplaceConfigurationValue(Name = "STARTUP_ADDRESS", ValueArg = nameof(address))]
        public void SetStartupAddress(int address) { }

        [ReplaceConfigurationValue(Name = "MCU_TYPE", ValueArg = nameof(type))]
        public void SetMCUType(string type) { }

        [ReplaceConfigurationValue(Name = "MCU", ValueArg =  nameof(mcu))]
        public void SetMCU(string mcu) { }

        // Определение размера Flash и RAM для генерации Linker Script
        // Чтобы код разработчика не "вылез" за границы выделенного сектора
        [ReplaceConfigurationValue(Name = "FLASH_SIZE", ValueArg =  nameof(flashSize))]
        [ReplaceConfigurationValue(Name = "RAM_SIZE", ValueArg =  nameof(ramSize))]
        public void SetMemoryLayout(uint flashSize, uint ramSize) { }

        // Версия стандарта C (например, "c11" или "c99")
        // Разные библиотеки могут требовать разный стандарт
        [ReplaceConfigurationValue(Name = "C_STANDARD", ValueArg =  nameof(version))]
        public void SetCStandard(string version) { }

        // Оптимизация (O0, O2, Os)
        // Для отладки Roslyn-кода лучше O0, для продакшена Os
        [ReplaceConfigurationValue(Name = "OPTIMIZATION", ValueArg =  nameof(level))]
        public void SetOptimization(string level) { }

        // Путь к файлу таблицы векторов (если пользователь хочет свои прерывания)
        [ReplaceConfigurationValue(Name = "VECTOR_TABLE_PATH", ValueArg =  nameof(path))]
        public void SetVectorTablePath(string path) { }
    }
}
