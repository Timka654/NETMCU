using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    //core
    public partial class BuildingContext
    {
        private async Task<bool> TryLoadCoreData(BuildingOptions bo)
        {
            var bc = this;

            if (!bo.Configurations.TryGetValue("CORE_PATH", out var corePath))
                throw new Exception("CORE_PATH is not defined in building options.");

            if (Directory.Exists(corePath))
                Console.WriteLine($"Core path exists: {corePath}");
            else
#if DEBUG
                corePath = "E:\\my_dev\\devmcu\\container\\core";
#else
                throw new Exception($"Core path does not exist: {corePath}");
#endif

            bo.Configurations["CFLAGS"] = string.Empty;

            bo.Configurations["EXECUTABLE_PROJECT_ROOT"] = RootPath;


            mcuCorePath = System.IO.Path.Combine(corePath, "mcu");

            StringBuilder dockerContentBuilder = new StringBuilder();

            var dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

            if (File.Exists(dockerFilePath))
                dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));

            if (bo.Configurations.TryGetValue("MCU_TYPE", out var mcu_type))
            {
                mcuCorePath = System.IO.Path.Combine(mcuCorePath, mcu_type);

                dockerFilePath = System.IO.Path.Combine(mcuCorePath, "Dockerfile.netmcu");

                if (File.Exists(dockerFilePath))
                    dockerContentBuilder.AppendLine(File.ReadAllText(dockerFilePath));
                else
                    throw new Exception($"MCU specific Dockerfile not found: {dockerFilePath}");
            }
            else if (type == BuildingOutputTypeEnum.Executable)
                throw new Exception("MCU_TYPE is not defined in building options.");

            if (!bo.Configurations.TryAdd("CFLAGS", string.Empty))
                bo.Configurations["CFLAGS"] = bo.Configurations["CFLAGS"].Trim();

            bo.Configurations["CFLAGS_DEFINES"] = string.Join(" ", bo.Defines.Select(d =>
            {
                var f = d.Key;
                if (!string.IsNullOrWhiteSpace(d.Value))
                    f += $"={d.Value}";
                return $"-D{f}";
            }));

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_DEFINES"]}";

            var libs = string.Join($" \\\n", bo.Libraries.GroupBy(x => x).Select(x => x.Key));

            if (!string.IsNullOrWhiteSpace(libs))
                libs += $" \\";

            bo.Configurations["CFLAGS_LIBS"] = libs;

            bo.Configurations["CFLAGS_INCLUDES"] = string.Join(" ", bo.Include.Select(inc => $"-I{inc}").GroupBy(x => x).Select(x => x.Key));

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_INCLUDES"]}";

            bo.Configurations["CFLAGS_MCU"] = bo.Configurations.TryGetValue("MCU", out var _mcu) ? $"-mcpu={_mcu}" : "";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_MCU"]}";

            bo.Configurations["CFLAGS_OPTIMIZATION"] = bo.Configurations.TryGetValue("OPTIMIZATION", out var _optimization) ? $"-{_optimization}" : "-O2";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_OPTIMIZATION"]}";

            bo.Configurations["CFLAGS_STARTUP_ADDRESS"] = bo.Configurations.TryGetValue("STARTUP_ADDRESS", out var _startup_address) ? $"-DUSER_CODE_ADDR={_startup_address}" : "";

            bo.Configurations["CFLAGS"] = $"{bo.Configurations["CFLAGS"].Trim()} {bo.Configurations["CFLAGS_STARTUP_ADDRESS"]}";

            bo.Configurations["CFLAGS"] = bo.Configurations["CFLAGS"].Trim();

            bo.Configurations["GIT_CLONE_COMMANDS"] = string.Join('\n', bo.GitRepositories.Select(repo =>
            {
                var url = repo.Url ?? throw new Exception("Repository URL is not defined.");
                var branch = repo.Branch ?? "main";
                return $"RUN git clone --branch {branch} --depth {(repo.Depth ?? 1)} {url} \"{repo.Path}\"";
            }));

            bo.Configurations["PACKAGE_INSTALL_COMMANDS"] = string.Join('\n', bo.Packages.Select(pkg => $"RUN apt-get install -y {pkg}"));

            dockerContent = dockerContentBuilder.ToString();



            mcuBinPath = System.IO.Path.Combine(this.BinPath, "NETMCU");

            if (!Directory.Exists(mcuBinPath))
                Directory.CreateDirectory(mcuBinPath);

            var objCorePath = System.IO.Path.Combine(bc.ObjPath, "NETMCU");

            if (!Directory.Exists(objCorePath))
                Directory.CreateDirectory(objCorePath);

            needsRebuildCore = false;

            if (type != BuildingOutputTypeEnum.Executable) return true;


            bo.BuildConfigurations();

            commonCorePath = System.IO.Path.Combine(mcuCorePath, "common.netmcu");

            if (!File.Exists(commonCorePath))
                throw new Exception($"Common core file not found: {commonCorePath}");

            commonData = JsonSerializer.Deserialize<CommonFileModel>(File.ReadAllText(commonCorePath), JsonSerializerOptions.Web);

            foreach (var req in commonData.Parameters.RequiredValues)
            {
                if (!bo.Configurations.ContainsKey(req))
                    throw new Exception($"Required value '{req}' is not defined in building options.");
            }
            foreach (var def in commonData.Parameters.DefaultValues)
            {
                if (!bo.Configurations.ContainsKey(def.Key))
                    bo.Configurations[def.Key] = def.Value;
            }

            dockerContent = bo.FillConfiguration(dockerContent, out var ic, out var ir);

            foreach (var item in bo.InputConfigurations)
            {
                if (bo.Configurations.TryGetValue(item.Name, out var iVal))
                {
                    var validate = item.Type switch
                    {
                        "float" => double.TryParse(iVal, CultureInfo.InvariantCulture, out _),
                        "number" => long.TryParse(iVal, CultureInfo.InvariantCulture, out _),
                        "string" => true,
                        "bool" => bool.TryParse(iVal, out _),
                        _ => throw new Exception($"Unsupported input configuration type: {item.Type} for configuration '{item.Name}'")
                    };

                    if (!validate)
                    {
                        item.Messages.TryGetValue("INVALID_TYPE", out var msg);
                        throw new Exception(msg ?? $"Input configuration '{item.Name}' has invalid value format '{iVal}' for type '{item.Type}'.");
                    }

                    if (item.ValidValues != null && !item.ValidValues.Contains(iVal))
                    {
                        item.Messages.TryGetValue("INVALID_VALUE", out var msg);
                        throw new Exception(msg ?? $"Input configuration '{item.Name}' has invalid value '{iVal}'. Valid values are: {string.Join(", ", item.ValidValues.Select(x => $"\"{x}\""))}.");
                    }
                }
                else
                {
                    if (item.Required)
                    {
                        item.Messages.TryGetValue("REQUIRED_VALUE", out var msg);
                        throw new Exception(msg ?? $"Required input configuration '{item.Name}' is not defined in building options.");
                    }

                    if (item.DefaultValue != default)
                        bo.Configurations[item.Name] = item.DefaultValue;
                }
            }


            mcuObjCorePath = System.IO.Path.Combine(objCorePath, "mcu_core", mcu_type);

            if (!Directory.Exists(mcuObjCorePath))
                Directory.CreateDirectory(mcuObjCorePath);

            // Генерируем временный Dockerfile
            tempDockerfilePath = System.IO.Path.Combine(mcuObjCorePath, "Dockerfile");

            string oldDockerfileContent = "";

            if (File.Exists(tempDockerfilePath))
            {
                oldDockerfileContent = File.ReadAllText(tempDockerfilePath);
                needsRebuildCore = dockerContent != oldDockerfileContent;
            }
            else
                needsRebuildCore = true;

            commonOldPath = System.IO.Path.Combine(mcuObjCorePath, "common.netmcu");

            string commonOldContent = "";

            if (File.Exists(commonOldPath))
            {
                commonOldContent = File.ReadAllText(commonOldPath);

                needsRebuildCore = needsRebuildCore || (File.ReadAllText(commonOldPath) != File.ReadAllText(commonCorePath));
            }
            else
                needsRebuildCore = true;

            buildDir = System.IO.Path.Combine(mcuObjCorePath, "build");

            needsRebuildCore = needsRebuildCore || !Directory.Exists(buildDir);

            return true;
        }

        public async Task<bool> BuildCore()
        {
            var bo = Options;

            if (needsRebuildCore)
            {
                Directory.Delete(mcuObjCorePath, true);
                Directory.CreateDirectory(mcuObjCorePath);

                DirectoryCopy(mcuCorePath, mcuObjCorePath, true, "(?<!\\.netmcu)$");

                var coreNativePath = System.IO.Path.Combine(mcuObjCorePath, "native");
                Directory.CreateDirectory(coreNativePath);

                foreach (var refCtx in CollectReferences())
                {
                    var refNativeDir = System.IO.Path.Combine(refCtx.BinPath, "NETMCU", "native");
                    if (Directory.Exists(refNativeDir))
                    {
                        DirectoryCopy(refNativeDir, coreNativePath, true);
                    }
                }

                File.WriteAllText(tempDockerfilePath, dockerContent);

                Regex parameterProcessing = new Regex($"%#(\\S+)#%");

                foreach (var path in commonData.Parameters.ProcessingPathes.Prepend("build.netmcu.sh"))
                {
                    var files = Directory.GetFiles(mcuObjCorePath, path);

                    foreach (var file in files)
                    {
                        var content = File.ReadAllText(file);
                        var newContent = parameterProcessing.Replace(content, match =>
                        {
                            var key = match.Value.Substring(2, match.Value.Length - 4).Trim();
                            if (bo.Configurations.TryGetValue(key, out var value))
                                return value;
                            else
                                return match.Value; // Оставляем без изменений, если не найдено
                        });
                        File.WriteAllText(file, newContent);
                        content = File.ReadAllText(file);
                    }

                }


                var pathes = new List<string>() {
                $"-v \"{mcuObjCorePath}:/project\""
                };

                pathes.AddRange(bo.Drives.Select(d => $"-v \"{d.Path}:{d.ContainerPath}\""));

                // Собираем образ с тегом 'final-mcu' из сгенерированного Dockerfile
                var buildArgs = $"build -t {commonData.ImageName} -f \"{tempDockerfilePath}\" \"{mcuObjCorePath}\"";
                var buildProcess = Process.Start("docker", buildArgs);
                buildProcess.WaitForExit();

                if (buildProcess.ExitCode != 0)
                {
                    throw new Exception($"Failed to build Docker image '{commonData.ImageName}'. Check your Dockerfile.");
                }

                Console.WriteLine("Building Core...");

                var coreBuildCmd = $"run --rm {string.Join(" ", pathes)} {commonData.ImageName} sh /project/build.netmcu.sh";
                // Запуск (исправил кавычки для безопасности в shell)
                var process = Process.Start("docker", coreBuildCmd);

                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception("Failed to build MCU core inside Docker container.");

                File.Copy(commonCorePath, commonOldPath, true);

                Console.WriteLine($"Ядро собрано.");
            }


            // После сборки парсим результат
            CoreSymbols = Stm32MapParser.ParseSymbols(System.IO.Path.Combine(buildDir, "kernel.map"));

            if (!CoreSymbols.ContainsKey("main"))
                return false;

            Console.WriteLine($"Точка входа ядра: 0x{CoreSymbols["main"]:X}");

            return true;
        }
    }
}
