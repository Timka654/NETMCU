using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Reflection;

namespace NETMCUCompiler.CodeBuilder
{
    public class LibraryCompiler
    {
        record TempDataRecord(SyntaxNode RootNode
            , SemanticModel SemanticModel
            , Dictionary<string, TypeCompilationContext> Classes
            , Dictionary<string, TypeCompilationContext> Structs
            , EnumDeclarationSyntax[] Enums
            , FieldDeclarationSyntax[] Consts
            , Dictionary<string, MethodCompilationContext> Methods);

        public static string GetFullNodeName(SyntaxNode node)
        {
            var names = new Stack<string>();

            var current = node;

            while (current != null)
            {
                if (current is BaseTypeDeclarationSyntax classDecl) // class, struct, enum
                    names.Push(classDecl.Identifier.Text);
                else if (current is MethodDeclarationSyntax methodDecl) // method
                    names.Push(methodDecl.Identifier.Text);
                else if (current is LocalFunctionStatementSyntax localFuncDecl) // local function
                    names.Push(localFuncDecl.Identifier.Text);
                else if (current is EnumMemberDeclarationSyntax enumDecl) // enum
                    names.Push(enumDecl.Identifier.Text);
                else if (current is BaseNamespaceDeclarationSyntax nsDecl) // namespace
                    names.Push(nsDecl.Name.ToString());
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax varDecl) // var
                    names.Push(varDecl.Identifier.Text);
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax) { }
                else if (current is Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax) { }
                else if (current is CompilationUnitSyntax) { }
                else if (current is BlockSyntax) { }
                else
                    throw new InvalidCastException($"Unsupported syntax node type: {current.GetType().FullName}");


                current = current.Parent;

                ////nameof(EnumDeclarationSyntax.Identifier)
                ////nameof(MethodDeclarationSyntax.Identifier)
                //nameof(FieldDeclarationSyntax.ToFullString)
            }

            return string.Join(".", names);
        }

        public static void CompileProject(Compilation compilation, CompilationContext context)
        {

            //IEnumerable<MethodDeclarationSyntax> GetInnerMethods(MethodDeclarationSyntax method)
            //{ 
            //method.Body.
            //}


            var items = compilation.SyntaxTrees.Select(tree =>
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);
                var rootDesc = root.DescendantNodes();

                var types = rootDesc
                .OfType<TypeDeclarationSyntax>()
                .ToArray();

                var classes = types.OfType<ClassDeclarationSyntax>()
                .Where(type => !context.ExceptTypes.Contains(type))
                .OrderByDescending(x => x == context.ProgramClass)
                .Select(x => new TypeCompilationContext(x) { SemanticModel = model, Name = GetFullNodeName(x), ParentContext = context })
                .ToDictionary(x => x.Name, x => x);

                var structs = types.OfType<StructDeclarationSyntax>()
                .Where(type => !context.ExceptTypes.Contains(type))
                .Select(x =>
                new TypeCompilationContext(x) { SemanticModel = model, Name = GetFullNodeName(x), ParentContext = context })
                .ToDictionary(x => x.Name, x => x); ;

                var enums = rootDesc.OfType<EnumDeclarationSyntax>()
                .ToArray();

                var consts = rootDesc.OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                .ToArray();

                var methods = rootDesc.OfType<MethodDeclarationSyntax>()
                .Cast<SyntaxNode>()
                .Concat(rootDesc.OfType<LocalFunctionStatementSyntax>())
                .Where(method =>
                {
                    var containingMethod = method.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (containingMethod != null && context.ExceptMethods.Contains(containingMethod))
                        return false;

                    if (method is MethodDeclarationSyntax mds && context.ExceptMethods.Contains(mds))
                        return false;

                    var containingType = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    if (containingType != null && context.ExceptTypes.Contains(containingType))
                        return false;

                    return true;
                })
                .OrderByDescending(x => x == context.MainMethod)
                .Select(x =>
                {

                    var p = GetFullNodeName(x.Parent);

                    TypeCompilationContext tcc = null;

                    if (x.Parent is ClassDeclarationSyntax)
                    {
                        if (!classes.TryGetValue(p, out tcc))
                            throw new Exception($"Не удалось найти класс {p}");
                    }
                    else if (x.Parent is StructDeclarationSyntax)
                    {
                        if (!structs.TryGetValue(p, out tcc))
                            throw new Exception($"Не удалось найти структуру {p}");
                    }
                    else if (x.Parent is BlockSyntax bs && bs.Parent is MethodDeclarationSyntax) { }
                    else throw new Exception($"Родительский узел метода должен быть классом или структурой, найдено: {x.Parent.GetType().FullName}");

                    var c = new MethodCompilationContext() { MethodSyntax = x, Name = GetFullNodeName(x), ParentContext = tcc };

                    return c;
                })
                .ToDictionary(x => x.Name, x => x);

                return new TempDataRecord(root, model, classes, structs, enums, consts, methods);
            }).ToArray();


            List<SyntaxNode> parents = new List<SyntaxNode>();

            foreach (var item in items)
            {
                foreach (var @enum in item.Enums)
                {
                    var isPublic = @enum.Modifiers.Any(m => m.Text == "public");

                    string? prefix = null;

                    BaseCompilationContext ec = context;

                    if (@enum.Parent is not NamespaceDeclarationSyntax)
                    {
                        var i = @enum.Parent;

                        do
                        {
                            if (i is ClassDeclarationSyntax cls)
                            {
                                isPublic = isPublic && cls.Modifiers.Any(m => m.Text == "public");
                                item.Classes.TryGetValue(GetFullNodeName(i), out var @class);
                                ec = @class;
                            }
                            if (i is StructDeclarationSyntax str)
                            {
                                isPublic = isPublic && str.Modifiers.Any(m => m.Text == "public");
                                item.Structs.TryGetValue(GetFullNodeName(i), out var @struct);
                                ec = @struct;
                            }
                            i = i.Parent;
                        } while (i != null);
                    }

                    foreach (var member in @enum.Members)
                    {
                        // Получаем значение через семантическую модель (Roslyn сам посчитает 0, 1, 2...)
                        var symbol = item.SemanticModel.GetDeclaredSymbol(member);

                        if (symbol?.ConstantValue != null)
                        {
                            var fullName = GetFullNodeName(member);
                            var val = symbol.ConstantValue;

                            if (isPublic)
                                context.RegisterConstant(fullName, val);
                            else
                                (ec as TypeCompilationContext).RegisterConstant(fullName, val);
                        }
                    }
                }

                foreach (var @const in item.Consts)
                {
                    var isPublic = @const.Modifiers.Any(m => m.Text == "public");

                    BaseCompilationContext ec = context;

                    if (@const.Parent is not NamespaceDeclarationSyntax)
                    {
                        var i = @const.Parent;

                        do
                        {
                            if (i is ClassDeclarationSyntax cls)
                            {
                                isPublic = isPublic && cls.Modifiers.Any(m => m.Text == "public");
                                item.Classes.TryGetValue(GetFullNodeName(i), out var @class);
                                ec = @class;
                            }
                            if (i is StructDeclarationSyntax str)
                            {
                                isPublic = isPublic && str.Modifiers.Any(m => m.Text == "public");
                                item.Structs.TryGetValue(GetFullNodeName(i), out var @struct);
                                ec = @struct;
                            }
                            i = i.Parent;
                        } while (i != null);
                    }

                    foreach (var @var in @const.Declaration.Variables)
                    {
                        if (@var.Initializer?.Value is LiteralExpressionSyntax literal)
                        {
                            var fullName = GetFullNodeName(@var);
                            int val = ASMInstructions.ParseLiteral(literal);

                            if (isPublic)
                                context.RegisterConstant(fullName, val);
                            else
                                (ec as TypeCompilationContext).RegisterConstant(fullName, val);
                        }
                    }
                }

                foreach (var c in item.Classes)
                {
                    context.Childs.Add(c.Key, c.Value);
                }

                foreach (var c in item.Structs)
                {
                    context.Childs.Add(c.Key, c.Value);
                }

                foreach (var c in item.Methods)
                {
                    if (c.Value.ParentContext == null)
                    {
                        if ((c.Value.MethodSyntax.Parent is BlockSyntax bs && bs.Parent is MethodDeclarationSyntax md))
                        {
                            var fullname = GetFullNodeName(md);
                            item.Methods.TryGetValue(fullname, out var pMethod);

                            c.Value.ParentContext = pMethod;
                        }
                        else throw new Exception($"Родительский узел метода должен быть классом или структурой, найдено: {c.Value.MethodSyntax.Parent.GetType().FullName}");
                    }

                    c.Value.ParentContext.Childs.Add(c.Key, c.Value);
                }
            }

            foreach (var item in items)
            {
                context.SemanticModel = item.SemanticModel;

                foreach (var method in item.Methods)
                {
                    CompileMethod(method.Value);
                }
            }
        }

        private static void CompileMethod(MethodCompilationContext method)
        {
            var methodSyntax = method.MethodSyntax as MethodDeclarationSyntax;
            var localFuncSyntax = method.MethodSyntax as LocalFunctionStatementSyntax;

            var body = methodSyntax?.Body ?? localFuncSyntax?.Body;
            var expressionBody = methodSyntax?.ExpressionBody ?? localFuncSyntax?.ExpressionBody;

            if (body == null && expressionBody == null) return;

            var modifiers = methodSyntax?.Modifiers ?? localFuncSyntax?.Modifiers;
            
            // СБОР ЛОКАЛЬНЫХ КОНСТАНТ МЕТОДА (вторая часть BuildAsm)
            var localConsts = method.MethodSyntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                                .Where(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)));

            foreach (var localConst in localConsts)
            {
                foreach (var v in localConst.Declaration.Variables)
                {
                    if (v.Initializer?.Value is LiteralExpressionSyntax lit)
                        method.RegisterConstant(v.Identifier.Text, ASMInstructions.ParseLiteral(lit));
                }
            }


            var methodSymbol = method.SemanticModel.GetDeclaredSymbol(method.MethodSyntax) as IMethodSymbol;

            // Полное имя: Namespace.ClassName.MethodName
            string fullName = methodSymbol.ToDisplayString();

            bool isStatic = modifiers.Value.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

            // Настраиваем фрейм (с учетом 'this' если метод не static)
            ASMInstructions.EmitMethodPrologue(!isStatic, method);

            var declarations = method.MethodSyntax.DescendantNodes().OfType<VariableDeclarationSyntax>();
            foreach (var decl in declarations)
            {
                // 1. Пытаемся получить символ типа через семантическую модель
                var typeSymbol = method.SemanticModel.GetTypeInfo(decl.Type).Type;

                // Если по какой-то причине символ не определен, откатываемся к ToString()
                // Но для 'var' здесь уже будет реальное имя (например, GPIO_InitTypeDef)
                string typeName = typeSymbol?.ToDisplayString() ?? decl.Type.ToString();

                foreach (var v in decl.Variables)
                {
                    // Теперь ctx получит правильное имя типа даже для var
                    method.AllocateOnStack(v.Identifier.Text, typeName);
                }
            }
            // Пользуемся нашим старым добрым билдером для внутренностей
            var builder = new Stm32MethodBuilder(method);
            if (body != null)
                builder.Visit(body);
            if (expressionBody != null)
                builder.Visit(expressionBody);

            ASMInstructions.EmitMethodEpilogue(method);
        }
    }
}
