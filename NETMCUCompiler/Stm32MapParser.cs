using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NETMCUCompiler
{
    public class Stm32MapParser
    {
        // Регулярка для GNU Linker (arm-none-eabi-gcc)
        // Ищет: адрес, пробелы, название секции или функции
        private static readonly Regex SymbolLine = new Regex(@"^\s+(0x[0-9a-fA-F]{8,16})\s+([a-zA-Z_][a-zA-Z0-9_]*)$", RegexOptions.Compiled);

        public static Dictionary<string, long> ParseSymbols(string mapFilePath)
        {
            var symbols = new Dictionary<string, long>();

            foreach (var line in File.ReadLines(mapFilePath))
            {
                var match = SymbolLine.Match(line);
                if (match.Success)
                {
                    var addrStr = match.Groups[1].Value;
                    var name = match.Groups[2].Value;

                    if (long.TryParse(addrStr.Replace("0x", ""), NumberStyles.HexNumber, null, out long addr))
                    {
                        symbols[name] = addr;
                    }
                }
            }
            return symbols;
        }
    }
}
