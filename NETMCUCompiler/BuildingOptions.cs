using NETMCUCompiler.CodeBuilder;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    public sealed class BuildingInputConfigurationModel
    {
        public string Name { get; set; }

        public string Type { get; set; } = "string";

        public string? DefaultValue { get; set; }

        public bool Required { get; set; }

        public string[]? ValidValues { get; set; }

        public Dictionary<string, string>? Messages { get; set; }
    }

    public class DriveConfiguration
    {
        public string Path { get; set; }

        public string ContainerPath { get; set; }
    }

    public class GitRepositoryConfiguration
    {
        public string Path { get; set; }

        public string Url { get; set; }

        public string? Branch { get; set; }

        public int? Depth { get; set; }
    }

    public class BuildingOptions
    {
        public Dictionary<string, string> Configurations { get; set; } = new()
            {
                { "STARTUP_ADDRESS", "0x08008000" },
                { "CORE_PATH", "" }
            };

        // Компилируем Regex один раз для производительности
        private static readonly Regex ConfigRegex = new Regex(@"%#(?<name>[^#%]+)#%", RegexOptions.Compiled);

        public List<string> FillConfiguration(List<string> templates, out int invalidCount, out List<string> invalidRecords)
        {
            var results = new List<string>(templates.Count);
            invalidCount = 0;
            invalidRecords = new List<string>();

            for (int i = 0; i < templates.Count; i++)
            {
                results.Add(FillConfiguration(templates[i], out int ic, out List<string> ir));
                invalidCount += ic;
                invalidRecords.AddRange(ir);
            }

            return results;
        }

        public string FillConfiguration(string template, out int invalidCount, out List<string> invalidRecords)
        {
            if (string.IsNullOrEmpty(template))
            {
                invalidCount = 0;
                invalidRecords = [];
                return template;
            }

            var ic = 0;
            var ir = new List<string>();

            var output = ConfigRegex.Replace(template, match =>
            {
                string key = match.Groups["name"].Value;

                // Если ключ есть в конфиге — заменяем, если нет — оставляем как было (или на string.Empty)
                if (Configurations.TryGetValue(key, out var value))
                    return value;
                ++ic;
                ir.Add(key);
                return match.Value;
            });

            invalidCount = ic;
            invalidRecords = ir;

            return output;
        }

        public void BuildConfigurations()
        {
            int i = 0, li = 0;

            while (true)
            {
                i = 0;
                foreach (var kvp in Configurations)
                {
                    var filledValue = FillConfiguration(kvp.Value, out int invalidCount, out List<string> invalidRecords);
                    if (invalidCount == 0)
                    {
                        Configurations[kvp.Key] = filledValue;
                        ++i;
                    }
                }

                if (li == i)
                {
                    // Если на итерации не было замен, значит все возможные замены сделаны, и можно выйти
                    break;
                }

                li = i;
            }

            Include = FillConfiguration(Include, out _, out _);
            Libraries = FillConfiguration(Libraries, out _, out _);
            Packages = FillConfiguration(Packages, out _, out _);
            Defines = Defines.ToDictionary(x => FillConfiguration(x.Key, out _, out _), x => FillConfiguration(x.Key, out _, out _));
            Drives = Drives.Select(x=>{
                x.Path = FillConfiguration(x.Path, out _, out _);
                x.ContainerPath = FillConfiguration(x.ContainerPath, out _, out _);
                return x;
            }).ToList();

            GitRepositories = GitRepositories.Select(repo =>
            {
                repo.Path = FillConfiguration(repo.Path, out _, out _);
                repo.Url = FillConfiguration(repo.Url, out _, out _);
                repo.Branch = FillConfiguration(repo.Branch, out _, out _);
                return repo;
            }).ToList();

            InputConfigurations = InputConfigurations.Select(input =>
            {
                input.Name = FillConfiguration(input.Name, out _, out _);
                input.Type = FillConfiguration(input.Type, out _, out _);
                input.DefaultValue = FillConfiguration(input.DefaultValue ?? string.Empty, out _, out _);
                if (input.ValidValues != null)
                    input.ValidValues = FillConfiguration(input.ValidValues.ToList(), out _, out _).ToArray();
                if (input.Messages != null)
                    input.Messages = input.Messages.ToDictionary(x => FillConfiguration(x.Key, out _, out _), x => FillConfiguration(x.Value, out _, out _));
                return input;
            }).ToList();
        }

        public List<string> Include { get; set; } = new();
        public List<string> Libraries { get; set; } = new();
        public List<string> Packages { get; set; } = new();
        public Dictionary<string, string> Defines { get; set; } = new();
        public List<DriveConfiguration> Drives { get; set; } = new();
        public List<GitRepositoryConfiguration> GitRepositories { get; set; } = new();

        public List<BuildingInputConfigurationModel> InputConfigurations { get; set; } = new();
    }
}
