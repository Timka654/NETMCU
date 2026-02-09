using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace NETMCUCompiler.CodeBuilder
{
    public class MethodCompilationContext : BaseCompilationContext
    {
        public required SyntaxNode MethodSyntax { get; set; }

        public required string Name { get; set; }

        public bool IsStatic { get; }

        public bool IsPublic { get; }

        public MethodCompilationContext()
        {
            if (MethodSyntax is MethodDeclarationSyntax methodDecl)
            {
                IsPublic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                IsStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
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
            => Class.Global.AddRelocation(this, name, isStatic || isNative, isNative, (int)Bin.Position);
            

        // Храним переменные, которые живут на стеке
        public Dictionary<string, StackVariable> StackMap { get; } = new();

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
            var t = SemanticModel.GetSymbolInfo(syntax);

            if (TryGetConstant(t.Symbol.ToDisplayString(), out value)) return true;

            return false;
        }
    }
}
