// Copyright 2014 Frank A. Krueger
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//    http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using Mono.Cecil;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.TypeSystem;
using System.Reflection;
using Netjs.AstTransformers;
using ICSharpCode.NRefactory.CSharp.Resolver;

namespace Netjs
{
    public class App : IAssemblyResolver
    {
        string asmDir = "";

        class Config
        {
            public string MainAssembly = "";
            public bool ShowHelp = false;
        }

        public static int Main(string[] args)
        {
            var config = new Config();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--help":
                    case "-help":
                    case "-?":
                        config.ShowHelp = true;
                        break;
                    default:
                        config.MainAssembly = a;
                        break;
                }
            }
            try
            {
                new App().Run(config);
                return 0;
            }
            catch (Exception ex)
            {
                Error("{0}", ex);
                return 1;
            }
        }

        void Run(Config config)
        {
            if (config.ShowHelp)
            {
                Console.WriteLine("Netjs compiler, Copyright 2014 Frank A. Krueger");
                Console.WriteLine("netjs [options] assembly-file");
                Console.WriteLine("   -help                Lists all compiler options (short: -?)");
                return;
            }

            if (string.IsNullOrEmpty(config.MainAssembly))
            {
                throw new Exception("No assembly specified.");
            }

            var asmPath = Path.GetFullPath(config.MainAssembly);
            asmDir = Path.GetDirectoryName(asmPath);
            var outPath = Path.ChangeExtension(asmPath, ".ts");

            string sourceCode = decompileAssembly(asmPath);
            File.WriteAllText("temp.cs", sourceCode);
            var syntaxTree = new CSharpParser().Parse(sourceCode, "temp.cs");
            var compilation = createCompilation(asmPath, syntaxTree);

            /*{
                var resolver = new CSharpAstResolver(compilation, syntaxTree);
                var expr = syntaxTree.Descendants.OfType<Expression>().Where(e => (e.ToString()) == "(text + \"\\0\")").First();
                Console.WriteLine(resolver.Resolve(expr).Type.FullName);
            }*/
            Step("Translating C# to TypeScript");
            CsToTs.Run(compilation, syntaxTree);

            Step("Writing");
            using (var outputWriter = new StreamWriter(outPath))
            {
                syntaxTree.AcceptVisitor(new ICSharpCode.NRefactory.CSharp.InsertParenthesesVisitor { InsertParenthesesForReadability = true });
                syntaxTree.AcceptVisitor(new FromILSharp.TSOutputVisitor(outputWriter, FormattingOptionsFactory.CreateAllman()));
            }

            Step("Done");
        }

        public static IAstTransform[] CreatePipeline(DecompilerContext context)
        {
            // from ICSharpCode.Decompiler.Ast.Transforms.TransformationPipeline
            return new IAstTransform[] {
                new PushNegation(),
                new DelegateConstruction(context),
                new PatternStatementTransform(context),
                new ReplaceMethodCallsWithOperators(context),
                new IntroduceUnsafeModifier(),
                new AddCheckedBlocks(),
                new DeclareVariables(context), // should run after most transforms that modify statements
				new ConvertConstructorCallIntoInitializer(), // must run after DeclareVariables
				new DecimalConstantTransform(),
                new IntroduceUsingDeclarations(context),
                //new IntroduceExtensionMethods(context), // must run after IntroduceUsingDeclarations
				//new IntroduceQueryExpressions(context), // must run after IntroduceExtensionMethods
				//new CombineQueryExpressions(context),
                //new FlattenSwitchBlocks(),
                new FixBadNames()
            };
        }

        ICompilation createCompilation(string mainPath, SyntaxTree tree)
        {
            List<IUnresolvedAssembly> assemblies = new List<IUnresolvedAssembly>();
            var unresolved = tree.ToTypeSystem();
            IProjectContent project = new CSharpProjectContent();
            string[] paths = {/* mainPath, */typeof(String).Assembly.Location, typeof(INotifyPropertyChanged).Assembly.Location,
                typeof(Enumerable).Assembly.Location, typeof(System.Drawing.Bitmap).Assembly.Location };
            AssemblyLoader loader = AssemblyLoader.Create();
            return project.AddOrUpdateFiles(unresolved)
                .AddAssemblyReferences(paths.Select(path => loader.LoadAssemblyFile(path)))
                .CreateCompilation();
        }

        string decompileAssembly(string path)
        {
            Step("Reading IL");
            var parameters = new ReaderParameters
            {
                AssemblyResolver = this,
            };
            var asm = AssemblyDefinition.ReadAssembly(path, parameters);
            mscorlib = AssemblyDefinition.ReadAssembly(typeof(String).Assembly.Location, parameters);
            system = AssemblyDefinition.ReadAssembly(typeof(INotifyPropertyChanged).Assembly.Location, parameters);
            systemCore = AssemblyDefinition.ReadAssembly(typeof(Enumerable).Assembly.Location, parameters);
            systemDrawing = AssemblyDefinition.ReadAssembly(typeof(System.Drawing.Bitmap).Assembly.Location, parameters);
            Step("Decompiling IL to C#");
            var context = new DecompilerContext(asm.MainModule);
            context.Settings.ForEachStatement = false;
            context.Settings.ObjectOrCollectionInitializers = false;
            context.Settings.UsingStatement = false;
            context.Settings.AsyncAwait = false;
            context.Settings.AutomaticProperties = true;
            context.Settings.AutomaticEvents = true;
            context.Settings.QueryExpressions = false;
            context.Settings.AlwaysGenerateExceptionVariableForCatchBlocks = true;
            context.Settings.UsingDeclarations = true;
            context.Settings.FullyQualifyAmbiguousTypeNames = true;
            context.Settings.YieldReturn = false;
            var builder = new AstBuilder(context);
            builder.AddAssembly(asm);

            foreach (var a in referencedAssemblies.Values)
            {
                if (a != null)
                    builder.AddAssembly(a);
            }
            {
                var type = asm.MainModule.Types.ElementAt(16);
                Console.WriteLine(type + "::");
                var astBuilder = new AstBuilder(new DecompilerContext(asm.MainModule) { CurrentType = type, Settings=context.Settings.Clone()});
                astBuilder.AddType(type);
                astBuilder.RunTransformations();
                var op = new PlainTextOutput();
                astBuilder.GenerateCode(op);
                Console.WriteLine(op.ToString());
            }
            foreach (var transform in CreatePipeline(context))
            {
                transform.Run(builder.SyntaxTree);
            }

            builder.SyntaxTree.AcceptVisitor(new ICSharpCode.NRefactory.CSharp.InsertParenthesesVisitor { InsertParenthesesForReadability = true });

            var str = new StringWriter();
            var outputFormatter = new TextTokenWriter(new PlainTextOutput(str), context) { FoldBraces = context.Settings.FoldBraces };
            builder.SyntaxTree.AcceptVisitor(new CSharpOutputVisitor(outputFormatter, context.Settings.CSharpFormattingOptions));
            return str.GetStringBuilder().ToString();
        }

        #region IAssemblyResolver implementation

        AssemblyDefinition mscorlib;
        AssemblyDefinition system;
        AssemblyDefinition systemCore;
        AssemblyDefinition systemDrawing;

        readonly Dictionary<string, AssemblyDefinition> referencedAssemblies = new Dictionary<string, AssemblyDefinition>();

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, null);
        }
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            switch (name.Name)
            {
                case "mscorlib":
                    return mscorlib;
                case "System":
                    return system;
                case "System.Core":
                    return systemCore;
                case "System.Drawing":
                    return systemDrawing;
                default:
                    var n = name.Name;
                    AssemblyDefinition asm;
                    if (!referencedAssemblies.TryGetValue(n, out asm))
                    {
                        var fn = Path.Combine(asmDir, name.Name + ".dll");
                        if (File.Exists(fn))
                        {

                            asm = parameters != null ?
                                AssemblyDefinition.ReadAssembly(fn, parameters) :
                                AssemblyDefinition.ReadAssembly(fn);
                            Info("  Loaded {0}", fn);
                        }
                        else
                        {
                            asm = null;
                            Error("  Could not find assembly {0}", name);
                        }
                        referencedAssemblies[n] = asm;
                    }
                    return asm;
            }
        }
        public AssemblyDefinition Resolve(string fullName)
        {
            return null;
        }
        public AssemblyDefinition Resolve(string fullName, ReaderParameters parameters)
        {
            return null;
        }
        #endregion

        #region console util
        public static void Step(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public static void Warning(string format, params object[] args)
        {
            Warning(string.Format(format, args));
        }

        public static void Warning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public static void Error(string format, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(format, args);
            Console.ResetColor();
        }

        public static void Info(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }
        #endregion
    }
}
