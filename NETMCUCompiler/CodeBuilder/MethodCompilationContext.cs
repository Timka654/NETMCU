using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;

namespace NETMCUCompiler.CodeBuilder
{
    public class MethodCompilationContext : BaseCompilationContext
    {
        public SyntaxNode MethodSyntax { get; }

        public required string Name { get; set; }

        public bool IsStatic { get; }

        public bool IsPublic { get; }

        public string? NativeName { get; }

        public bool IgnoreMethodCompilation => ParentContext is TypeCompilationContext typeCompilation && typeCompilation.CompilerType;

        public MethodCompilationContext(SyntaxNode methodSyntax, IMethodSymbol symbol)
        {
            MethodSyntax = methodSyntax;

            if (symbol != null)
            {
                IsPublic = symbol.DeclaredAccessibility == Accessibility.Public;
                IsStatic = symbol.IsStatic;
                NativeName = symbol.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name.Contains("NativeCall") == true)?
                    .ConstructorArguments.FirstOrDefault().Value?.ToString();
            }
        }

        public Dictionary<string, int> RegisterMap { get; } = new();

        public int NextFreeRegister = 4; // Пул R4-R11
        public int LastUsedRegister { get; set; } = 4; // Текущий "рабочий" регистр

        // Вспомогательный метод для получения регистра переменной
        public int GetVarRegister(string name)
        {
            if (RegisterMap.TryGetValue(name, out int reg)) return reg;
            throw new Exception($"Переменная {name} не объявлена");
        }

        // Вспомогательные методы, чтобы не писать каждый раз Asm.AppendLine
        public void Emit(string line) => Asm.AppendLine($"    {line}");

        public void Bytecode(byte code) => Bin.WriteByte(code);

        public StringBuilder Asm { get; } = new();
        public MemoryStream Bin { get; } = new();

        public int LabelCount = 0;
        // Вспомогательные методы, чтобы не путаться в байтах
        public void Write16(ushort val)
        {
            Bytecode((byte)(val & 0xFF));
            Bytecode((byte)(val >> 8));
        }

        // Исправленный Write32 для Thumb-2 Wide Instructions
        public void Write32(uint val)
        {
            // Сначала пишем High Halfword, потом Low Halfword
            ushort high = (ushort)(val >> 16);
            ushort low = (ushort)(val & 0xFFFF);
            Write16(high);
            Write16(low);
        }
        public string NextLabel(string prefix) => $"L_{prefix}_{LabelCount++}";

        public void AddRelocation(string name, bool isStatic, bool isNative)
        {
            Class.Global.AddRelocation(this, name, isStatic || isNative, isNative, (int)Bin.Position);
        }

        //public void EmitLoadAddress(string register, string symbolName)
        //{
        //    // Добавляем релокацию данных по текущему смещению в бинарном коде метода
        //    Class.Global.AddDataRelocation(this, symbolName, (int)Bin.Length);

        //    // Генерируем плейсхолдер для пары инструкций MOVW/MOVT
        //    Emit($"MOVW+MOVT {register}, =<symbol> ; placeholder for {symbolName}");
        //    Bin.Write(new byte[8], 0, 8); // Резервируем 8 байт
        //}

        public void AddDataRelocation(string symbolName)
        {
            Class.Global.AddDataRelocation(this, symbolName, (int)this.Bin.Length);
        }

        //public void EmitLoadStringAddress(string register, string symbolName)
        //{
        //    // Добавляем запись о релокации данных.
        //    Class.Global.AddDataRelocation(this, symbolName, (int)this.Bin.Length);

        //    // Генерируем ассемблерный плейсхолдер и резервируем 8 байт
        //    // для пары инструкций MOVW/MOVT.
        //    this.Emit($"LDR {register}, ={symbolName} ; (placeholder for MOVW/MOVT)");
        //    this.Bin.Write(new byte[8], 0, 8);
        //}

        // Храним переменные, которые живут на стеке
        public Dictionary<string, StackVariable> StackMap { get; } = new();

        public Dictionary<string, int> Labels { get; } = new();
        public List<JumpRecord> Jumps { get; } = new();

        public void MarkLabel(string label)
        {
            Asm.AppendLine($"{label}:");
            Labels[label] = (int)Bin.Position;
        }

        public void AddJump(string label, bool isConditional)
        {
            Jumps.Add(new JumpRecord { TargetLabel = label, Offset = (int)Bin.Position, IsConditional = isConditional });
        }

        public override CompilationContextTypeEnum ContextType => CompilationContextTypeEnum.Method;

        private int _currentStackPointer = 0;

        public void AllocateOnStack(string name, string typeName)
        {
            if (Class.Global.Childs.TryGetValue(typeName, out var meta))
            {
                if (meta is not TypeCompilationContext tcc) throw new Exception($"Тип {typeName} не является типом данных");

                StackMap[name] = new StackVariable
                {
                    Name = name,
                    Metadata = tcc,
                    StackOffset = _currentStackPointer
                };
                _currentStackPointer += tcc.Size;

                // Важно: в будущем нам нужно будет вычесть _currentStackPointer из SP 
                // в начале метода (Prologue), чтобы зарезервировать место.
            }
        }

        //public void RegisterMethod(string method, bool isStatic)
        //{
        //    var position = (int)Bin.Position;
        //    // Регистрируем точку входа в ExportMap
        //    foreach (var c in LinkerContexts)
        //    {
        //        c.OutputMethods[method] = new LinkerRecord(this, position, isStatic);
        //    }
        //}

        public TypeCompilationContext Class
        {
            get
            {
                if (ParentContext is TypeCompilationContext pc) return pc;
                if (ParentContext is MethodCompilationContext mcc) return mcc.Class;
                throw new Exception("Невозможно получить Class для данного контекста");
            }
        }

        public SemanticModel SemanticModel
            => Class.Global.SemanticModel;

        //LinkerContext[] LinkerContexts => Class.Global.LinkerContexts;

        public int? BinaryOffset { get; set; }

        private Dictionary<string, object> ConstantMap { get; } = new();

        public void RegisterConstant(string name, object value)
        {
            ConstantMap[name] = value;
        }

        public bool TryGetConstant(string name, out object value)
        {
            if (ConstantMap.TryGetValue(name, out value)) return true;
            return Class.TryGetConstant(name, out value);
        }

        public bool TryGetConstant(ExpressionSyntax syntax, out object value)
        {
            var constOpt = SemanticModel.GetConstantValue(syntax);
            if (constOpt.HasValue) 
            {
                value = constOpt.Value;
                return true;
            }

            var t = SemanticModel.GetSymbolInfo(syntax);

            if (t.Symbol != null && TryGetConstant(t.Symbol.ToDisplayString(), out value)) return true;

            value = null;
            return false;
        }

        public override string ToString()
        => Name;
    }
    public class JumpRecord
    {
        public string TargetLabel { get; set; } = null!;
        public int Offset { get; set; }
        public bool IsConditional { get; set; }
    }
}
