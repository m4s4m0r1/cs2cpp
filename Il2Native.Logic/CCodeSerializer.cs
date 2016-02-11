﻿namespace Il2Native.Logic
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using DOM;

    using Il2Native.Logic.Properties;

    using Microsoft.CodeAnalysis;
 
    public class CCodeSerializer
    {
        private string currentFolder;

        public bool Concurrent { get; set; }

        public void WriteTo(AssemblyIdentity identity, ISet<AssemblyIdentity> references, bool isCoreLib, IEnumerable<CCodeUnit> units, string outputFolder)
        {
            this.currentFolder = Path.Combine(outputFolder, identity.Name);
            if (!Directory.Exists(this.currentFolder))
            {
                Directory.CreateDirectory(this.currentFolder);
            }

            var includeHeaders = this.WriteTemplateSources(units);

            this.WriteHeader(identity, references, isCoreLib, units, includeHeaders);

            this.WriteCoreLibSource(identity, isCoreLib);

            this.WriteSources(identity, units);

            this.WriteBuildFiles(identity, references, !isCoreLib);
        }

        public void WriteBuildFiles(AssemblyIdentity identity, ISet<AssemblyIdentity> references, bool executable)
        {
            // CMake file helper
            var cmake = @"cmake_minimum_required (VERSION 2.8.10 FATAL_ERROR)

file(GLOB_RECURSE <%name%>_SRC
    ""./src/*.cpp""
)

include_directories(""./""<%include%>)

if (MSVC)
link_directories(""./""<%link_msvc%>)
SET(CMAKE_CXX_FLAGS ""${CMAKE_CXX_FLAGS} /Od /GR- /Zi /EHsc"")
else()
link_directories(""./""<%link_other%>)
SET(CMAKE_CXX_FLAGS ""${CMAKE_CXX_FLAGS} -O0 -g -gdwarf-4 -march=native -std=gnu++14 -fno-rtti -fpermissive"")
endif()

add_<%type%> (<%name%> ""${<%name%>_SRC}"")

<%libraries%>";

            var targetLinkLibraries = @"
if (MSVC)
target_link_libraries (<%name%> {0})
else()
target_link_libraries (<%name%> {0} ""stdc++"")
endif()";

            var type = executable ? "executable" : "library";
            var include = string.Join(" ", references.Select(a => string.Format("\"../{0}/src\"", a.Name.CleanUpNameAllUnderscore())));
            var link_msvc = string.Join(" ", references.Select(a => string.Format("\"../{0}/__build_win32_debug\"", a.Name.CleanUpNameAllUnderscore())));
            var link_other = string.Join(" ", references.Select(a => string.Format("\"../{0}/__build_mingw32_debug\"", a.Name.CleanUpNameAllUnderscore())));
            var libraries = string.Format(targetLinkLibraries, string.Join(" ", references.Select(a => string.Format("\"{0}\"", a.Name.CleanUpNameAllUnderscore()))));

            using (var itw = new IndentedTextWriter(new StreamWriter(this.GetPath("CMakeLists", ".txt"))))
            {
                itw.Write(
                    cmake.Replace("<%libraries%>", executable ? libraries : string.Empty)
                         .Replace("<%type%>", type)
                         .Replace("<%name%>", identity.Name.CleanUpNameAllUnderscore())
                         .Replace("<%include%>", include)
                         .Replace("<%link_msvc%>", link_msvc)
                         .Replace("<%link_other%>", link_other));
                itw.Close();
            }

            // build mingw32 DEBUG .bat
            var buildMinGw32 = @"md __build_mingw32_debug
cd __build_mingw32_debug
cmake -f .. -G ""MinGW Makefiles"" -DCMAKE_BUILD_TYPE=Debug -Wno-dev
mingw32-make";

            using (var itw = new IndentedTextWriter(new StreamWriter(this.GetPath("build_mingw32_debug", ".bat"))))
            {
                itw.Write(buildMinGw32.Replace("<%name%>", identity.Name.CleanUpNameAllUnderscore()));
                itw.Close();
            }

            // build Visual Studio .bat
            var buildVS2015 = @"md __build_win32_debug
cd __build_win32_debug
cmake -f .. -G ""Visual Studio 14"" -Wno-dev
call ""%VS140COMNTOOLS%\..\..\VC\vcvarsall.bat"" x86
MSBuild ALL_BUILD.vcxproj /p:Configuration=Debug /p:Platform=""Win32"" /toolsversion:14.0";

            using (var itw = new IndentedTextWriter(new StreamWriter(this.GetPath("build_vs2015_debug", ".bat"))))
            {
                itw.Write(buildVS2015.Replace("<%name%>", identity.Name.CleanUpNameAllUnderscore()));
                itw.Close();
            }
        }

        public IList<string> WriteTemplateSources(IEnumerable<CCodeUnit> units)
        {
            var headersToInclude = new List<string>();

            // write all sources
            foreach (var unit in units)
            {
                int nestedLevel;
                var path = this.GetPath(unit, out nestedLevel, ".h");
                var anyRecord = false;
                using (var itw = new IndentedTextWriter(new StreamWriter(path)))
                {
                    var c = new CCodeWriterText(itw);
                    foreach (var definition in unit.Definitions.Where(d => d.IsGeneric))
                    {
                        anyRecord = true;
                        definition.WriteTo(c);
                    }

                    itw.Close();
                }

                if (!anyRecord)
                {
                    File.Delete(path);
                }
                else
                {
                    headersToInclude.Add(path.Substring("src\\".Length + this.currentFolder.Length + 1));
                }
            }

            return headersToInclude;
        }

        public void WriteSources(AssemblyIdentity identity, IEnumerable<CCodeUnit> units)
        {
            if (this.Concurrent)
            {
                // write all sources
                Parallel.ForEach(
                    units.Where(unit => !((INamedTypeSymbol)unit.Type).IsGenericType),
                    (unit) =>
                    {
                        this.WriteSource(identity, unit);
                    });
            }
            else
            {
                // write all sources
                foreach (var unit in units.Where(unit => !((INamedTypeSymbol)unit.Type).IsGenericType))
                {
                    this.WriteSource(identity, unit);
                }                 
            }
        }

        private void WriteSource(AssemblyIdentity identity, CCodeUnit unit)
        {
            int nestedLevel;
            var path = this.GetPath(unit, out nestedLevel);

            var anyRecord = false;
            using (var itw = new IndentedTextWriter(new StreamWriter(path)))
            {
                var c = new CCodeWriterText(itw);

                WriteSourceInclude(itw, identity, nestedLevel);

                foreach (var definition in unit.Definitions.Where(d => !d.IsGeneric))
                {
                    anyRecord = true;
                    definition.WriteTo(c);
                }

                if (unit.MainMethod != null)
                {
                    WriteSourceMainEntry(c, itw, unit.MainMethod);
                }

                itw.Close();
            }

            if (!anyRecord)
            {
                File.Delete(path);
            }
        }

        public static void WriteSourceMainEntry(CCodeWriterBase c, IndentedTextWriter itw, IMethodSymbol mainMethod)
        {
            itw.WriteLine();
            itw.WriteLine("int main()");
            itw.WriteLine("{");
            itw.Indent++;
            c.WriteMethodFullName(mainMethod);
            itw.Write("(");
            if (mainMethod.Parameters.Length > 0)
            {
                itw.Write("nullptr");
            }

            itw.WriteLine(");");
            itw.WriteLine("return 0;");
            itw.Indent--;
            itw.WriteLine("}");
        }

        public static void WriteSourceInclude(IndentedTextWriter itw, AssemblyIdentity identity, int nestedLevel)
        {
            itw.Write("#include \"");
            for (var i = 0; i < nestedLevel; i++)
            {
                itw.Write("..\\");
            }

            itw.WriteLine("{0}.h\"", identity.Name);
        }

        public void WriteHeader(AssemblyIdentity identity, ISet<AssemblyIdentity> references, bool isCoreLib, IEnumerable<CCodeUnit> units, IList<string> includeHeaders)
        {
            // write header
            using (var itw = new IndentedTextWriter(new StreamWriter(this.GetPath(identity.Name, subFolder: "src"))))
            {
                var c = new CCodeWriterText(itw);

                if (isCoreLib)
                {
                    itw.WriteLine(Resources.c_include.Replace("<<%assemblyName%>>", identity.Name));
                }
                else
                {
                    foreach (var reference in references)
                    {
                        itw.WriteLine("#include \"{0}.h\"", reference.Name);
                    }
                }

                // write forward declaration
                foreach (var unit in units)
                {
                    var namedTypeSymbol = (INamedTypeSymbol)unit.Type;
                    foreach (var namespaceNode in namedTypeSymbol.ContainingNamespace.EnumNamespaces())
                    {
                        itw.Write("namespace ");
                        c.WriteNamespaceName(namespaceNode);
                        itw.Write(" { ");
                    }

                    if (namedTypeSymbol.IsGenericType)
                    {
                        c.WriteTemplateDeclaration(namedTypeSymbol);
                    }

                    itw.Write(namedTypeSymbol.IsValueType ? "struct" : "class");
                    itw.Write(" ");
                    c.WriteTypeName(namedTypeSymbol, false);
                    itw.Write("; ");

                    foreach (var namespaceNode in namedTypeSymbol.ContainingNamespace.EnumNamespaces())
                    {
                        itw.Write("}");
                    }

                    itw.WriteLine();

                    if (namedTypeSymbol.SpecialType == SpecialType.System_Object ||
                        namedTypeSymbol.SpecialType == SpecialType.System_String)
                    {
                        itw.Write("typedef ");
                        c.WriteType(namedTypeSymbol, true, true, false);
                        itw.Write(" ");
                        c.WriteTypeName(namedTypeSymbol);
                        itw.WriteLine(";");
                    }
                }

                itw.WriteLine();

                if (isCoreLib)
                {
                    itw.WriteLine(Resources.c_forward_declarations.Replace("<<%assemblyName%>>", identity.Name));
                }

                itw.WriteLine();

                // write full declaration
                foreach (var unit in units)
                {
                    var any = false;
                    var namedTypeSymbol = (INamedTypeSymbol)unit.Type;
                    foreach (var namespaceNode in namedTypeSymbol.ContainingNamespace.EnumNamespaces())
                    {
                        itw.Write("namespace ");
                        c.WriteNamespaceName(namespaceNode);
                        itw.Write(" { ");
                        any = true;
                    }

                    if (any)
                    {
                        itw.Indent++;
                        itw.WriteLine();
                    }

                    if (namedTypeSymbol.IsGenericType)
                    {
                        c.WriteTemplateDeclaration(namedTypeSymbol);
                    }

                    itw.Write(namedTypeSymbol.IsValueType ? "struct" : "class");
                    itw.Write(" ");
                    c.WriteTypeName(namedTypeSymbol, false);
                    if (namedTypeSymbol.BaseType != null)
                    {
                        itw.Write(" : public ");
                        c.WriteTypeFullName(namedTypeSymbol.BaseType, false);
                    }

                    if (namedTypeSymbol.TypeKind == TypeKind.Interface)
                    {
                        itw.Write(" : ");

                        if (namedTypeSymbol.Interfaces.Any())
                        {
                            var any2 = false;
                            foreach (var item in namedTypeSymbol.Interfaces)
                            {
                                if (any2)
                                {
                                    itw.Write(", ");
                                }

                                itw.Write("public virtual ");
                                c.WriteTypeFullName(item, false);

                                any2 = true;
                            }
                        }
                        else
                        {
                            itw.Write("public virtual object");
                        }
                    }

                    itw.WriteLine();
                    itw.WriteLine("{");
                    itw.WriteLine("public:");
                    itw.Indent++;

                    // base declaration
                    if (namedTypeSymbol.BaseType != null)
                    {
                        itw.Write("typedef ");
                        c.WriteTypeFullName(namedTypeSymbol.BaseType, false);
                        itw.WriteLine(" base;");
                    }

                    // declare using to solve issue with overloaded functions in different classes
                    foreach (var method in namedTypeSymbol.IterateAllMethodsWithTheSameNames())
                    {
                        c.TextSpan("using");
                        c.WhiteSpace();
                        c.WriteType(method.ReceiverType, true, true, true);
                        c.TextSpan("::");
                        c.WriteMethodName(method);
                        c.TextSpan(";");
                        c.NewLine();
                    }

                    if (namedTypeSymbol.TypeKind == TypeKind.Enum)
                    {
                        // value holder for enum
                        c.WriteType(namedTypeSymbol.EnumUnderlyingType, true);
                        itw.WriteLine(" m_value;");
                    }

                    if (!unit.HasDefaultConstructor)
                    {
                        c.WriteTypeName(namedTypeSymbol, false);
                        itw.WriteLine("() = default;");
                    }

                    foreach (var declaration in unit.Declarations)
                    {
                        declaration.WriteTo(c);
                    }

                    itw.Indent--;
                    itw.WriteLine("};");

                    foreach (var namespaceNode in namedTypeSymbol.ContainingNamespace.EnumNamespaces())
                    {
                        itw.Indent--;
                        itw.Write("}");
                    }

                    itw.WriteLine();
                }

                if (isCoreLib)
                {
                    itw.WriteLine();
                    itw.WriteLine(Resources.c_declarations.Replace("<<%assemblyName%>>", identity.Name));
                }

                itw.WriteLine();

                foreach (var includeHeader in includeHeaders)
                {
                    itw.WriteLine("#include \"{0}\"", includeHeader);
                }

                itw.Close();
            }
        }

        public void WriteCoreLibSource(AssemblyIdentity identity, bool isCoreLib)
        {
            if (!isCoreLib)
            {
                return;
            }

            // write header
            using (var itw = new IndentedTextWriter(new StreamWriter(this.GetPath(identity.Name, subFolder: "src", ext:".cpp"))))
            {
                WriteSourceInclude(itw, identity, 0);

                itw.WriteLine(Resources.c_definitions.Replace("<<%assemblyName%>>", identity.Name));
                itw.Close();
            }
        }

        private string GetPath(string name, string ext = ".h", string subFolder = "")
        {
            var fullDirPath = Path.Combine(this.currentFolder, subFolder);
            var fullPath = Path.Combine(fullDirPath, String.Concat(name, ext));
            if (!Directory.Exists(fullDirPath))
            {
                Directory.CreateDirectory(fullDirPath);
            }

            return fullPath;
        }

        private string GetPath(CCodeUnit unit, out int nestedLevel, string ext = ".cpp")
        {
            var fileRelativePath = GetRelativePath(unit, out nestedLevel);
            var fullDirPath = Path.Combine(this.currentFolder, "src", fileRelativePath);
            if (!Directory.Exists(fullDirPath))
            {
                Directory.CreateDirectory(fullDirPath);
            }

            var fullPath = Path.Combine(fullDirPath, String.Concat(((INamedTypeSymbol)unit.Type).GetTypeName().CleanUpNameAllUnderscore(), ext));
            return fullPath;
        }

        private static string GetRelativePath(CCodeUnit unit, out int nestedLevel)
        {
            var enumNamespaces = unit.Type.ContainingNamespace.EnumNamespaces().Where(n => !n.IsGlobalNamespace).ToList();
            nestedLevel = enumNamespaces.Count();
            return String.Join("\\", enumNamespaces.Select(n => n.MetadataName.ToString().CleanUpNameAllUnderscore()));
        }
    }
}
