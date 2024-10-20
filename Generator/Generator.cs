using System.Diagnostics;
using System.IO.Compression;
using Mono.Cecil;

public struct ImportLib {
    public string libName;
    public string prefix;
    public ImportLib(string libName, string prefix) {
        this.libName = libName;
        this.prefix = prefix;
    }
};

class Program {
    public static void Main (string[] args) {
        var program = new Program();
        program.Load();
        program.Generate();
    }

    HashSet<FieldDefinition> fields = new HashSet<FieldDefinition>();
    HashSet<MethodDefinition> functions = new HashSet<MethodDefinition>();
    HashSet<TypeDefinition> enums = new HashSet<TypeDefinition>();
    HashSet<TypeDefinition> structs = new HashSet<TypeDefinition>();
    HashSet<TypeDefinition> interfaces = new HashSet<TypeDefinition>();
    HashSet<TypeDefinition> functionTypes = new HashSet<TypeDefinition>();

    ImportLib[] importLibs = {
        new ImportLib("dwrite", "DWrite"),
        new ImportLib("d2d1", "D2D1"),
    };

    Program() {
    }

    void Load() {
        // https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/
        using var fs = File.OpenRead(File.Exists("Generator/Windows.Win32.winmd.gz") ? "Generator/Windows.Win32.winmd.gz" : "../../../Windows.Win32.winmd.gz");
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var stream = new MemoryStream();
        gz.CopyTo(stream);
        stream.Seek(0, SeekOrigin.Begin);
        using var module = ModuleDefinition.ReadModule(stream);

        string[] namespaces = {
            "Windows.Win32.Graphics.DirectWrite",
            "Windows.Win32.Graphics.Direct2D",
            "Windows.Win32.Graphics.Direct2D.Common",
        };
        foreach (var type in module.GetTypes()) {
            if (!namespaces.Contains(type.Namespace)) {
                continue;
            }

            var name = type.Name;
            if (name == "Apis") {
                foreach (var field in type.Fields) {
                    fields.Add(field);
                }
                foreach (var function in type.Methods) {
                    functions.Add(function);
                }
            } else if (type.IsEnum) {
                enums.Add(type);
            } else if (type.IsInterface) {
                interfaces.Add(type);
            } else if (type.IsClass && type.IsValueType) {
                structs.Add(type);
            } else if (type.BaseType.FullName == "System.MulticastDelegate") {
                functionTypes.Add(type);
            } else {
                throw new NotImplementedException();
            }
        }
    }

    string[] namesWin32 = {
        "IUnknown", "IStream",
        "HRESULT", "HANDLE", "HDC", "HMONITOR", "HWND",
        "BOOL", "RECT", "POINT", "SIZE", "COLORREF",
        "FILETIME", "LOGFONTW",
    };
    string ToOdinName(string name) {
        if (namesWin32.Contains(name)) {
            return "win32." + name;
        }
        if (name.StartsWith("IDXGI")) {
            return "dxgi.I" + name.Substring(5);
        }
        if (name.StartsWith("DXGI_")) {
            return "dxgi." + name.Substring(5);
        }
        if (name.StartsWith("D3D_")) {
            return "d3d11." + name.Substring(4);
        }
        return name;
    }

    string ResolveNameCollisions(string name, HashSet<string> names) {
        var resolvedName = name;
        int idx = 0;
        while (names.Contains(resolvedName)) {
            idx += 1;
            resolvedName = $"{name}{idx}";
        }
        names.Add(resolvedName);
        return resolvedName;
    }

    long GetSimpleIntSize(MetadataType type) {
        switch (type) {
            case MetadataType.SByte: return 8;
            case MetadataType.Byte: return 8;
            case MetadataType.Int16: return 16;
            case MetadataType.UInt16: return 16;
            case MetadataType.Int32: return 32;
            case MetadataType.UInt32: return 32;
            case MetadataType.Int64: return 64;
            case MetadataType.UInt64: return 64;
        }
        throw new InvalidOperationException();
    }

    string GetSimpleType(TypeReference type) {
        if (type.MetadataType == MetadataType.Void) { return ""; }
        if (type.FullName == "System.Guid") { return "win32.GUID"; }
        if (type.Name == "PWSTR") { return "^win32.WCHAR"; }
        if (type.Name == "PSTR") { return "^win32.CHAR"; }
        if (type.IsPrimitive) {
            switch (type.MetadataType) {
                case MetadataType.SByte: return "i8";
                case MetadataType.Byte: return "u8";
                case MetadataType.Int16: return "i16";
                case MetadataType.UInt16: return "u16";
                case MetadataType.Int32: return "i32";
                case MetadataType.UInt32: return "u32";
                case MetadataType.Int64: return "i64";
                case MetadataType.UInt64: return "u64";
                case MetadataType.Single: return "f32";
                case MetadataType.Double: return "f64";
                default:
                    throw new NotSupportedException();
            }
        }

        if (type.IsPointer) {
            var elementType = ((TypeSpecification)type).ElementType;
            if (elementType.MetadataType == MetadataType.Void) {
                return "rawptr";
            } else {
                return "^" + GetSimpleType(elementType);
            }
        }

        var odinName = ToOdinName(type.Name);
        if (type.Resolve().IsInterface) {
            odinName = "^" + odinName;
        }
        return odinName;
    }

    // See https://blog.airesoft.co.uk/2014/12/direct2d-scene-of-the-accident/
    bool IsReturnTypeFixNeeded(TypeReference retType) {
        var retTypeStr = GetSimpleType(retType);
        return retTypeStr != "win32.HRESULT" && retTypeStr != "win32.HANDLE" && retTypeStr != "win32.HDC" && retTypeStr != "win32.HWND" && retTypeStr != "win32.BOOL"
               && retType.IsValueType && !retType.IsPrimitive && !retType.Resolve().IsEnum;
    }

    string[] badVariableNames = { "context", "string", "defer", "matrix" };
    string SanitizeVariableName(string name) {
        if (badVariableNames.Contains(name)) return "_" + name;
        return name;
    }

    void WriteField(StreamWriter file, int level, FieldDefinition field) {
        string indent = new string('\t', level);
        var fieldName = SanitizeVariableName(field.Name);

        if (field.FieldType.IsArray) {
            var arrayType = (ArrayType)field.FieldType;
            Debug.Assert(arrayType.Dimensions.Count == 1);
            Debug.Assert(arrayType.Dimensions[0].LowerBound == 0);
            file.WriteLine($"{indent}{fieldName}: [{arrayType.Dimensions[0].UpperBound + 1}]{GetSimpleType(arrayType.ElementType)},");
            return;
        }

        // TODO special-case all the MATRIX_nXm_F types in D2D, and instead generate odin matrix types.

        var type = field.FieldType.Resolve();
        if (type.IsNested) {
            var isUnion = type.Fields.Count > 1;
            foreach (var nestedField in type.Fields) {
                isUnion &= nestedField.Offset == 0; // For structs, Offset seems to be -1 for every memeber...
            }

            file.Write($"{indent}");
            file.Write($"{fieldName}: struct");
            if (isUnion) {
                file.Write(" #raw_union");
            }
            file.WriteLine(" {");
            foreach (var nestedField in type.Fields) {
                WriteField(file, level + 1, nestedField);
            }
            file.WriteLine($"{indent}}},");
            return;
        }

        var bitfields = field.CustomAttributes
            .Where(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.NativeBitfieldAttribute")
            .OrderBy(attr => (long)attr.ConstructorArguments[1].Value)
            .ToArray();
        if (bitfields.Length != 0) {
            Debug.Assert(field.Name == "_bitfield");

            long nextOffset = 0;
            long totalSize = GetSimpleIntSize(type.MetadataType);

            var fieldType = GetSimpleType(field.FieldType);
            file.WriteLine($"{indent}using bitfield: bit_field {fieldType} {{");
            foreach (var bitfield in bitfields) {
                var args = bitfield.ConstructorArguments;
                var bitname = (string)args[0].Value;
                var offset = (long)args[1].Value;
                var length = (long)args[2].Value;
                Debug.Assert(offset == nextOffset);
                Debug.Assert(offset + length <= totalSize);
                nextOffset = offset + length;

                file.WriteLine($"{indent}\t{bitname}: {fieldType} | {length},");
            }
            file.WriteLine($"{indent}}},");
            return;
        }

        file.WriteLine($"{indent}{fieldName}: {GetSimpleType(field.FieldType)},");
    }

    void Generate() {
        using (var file = File.CreateText("dwrite.odin")) {
            file.WriteLine(@"package dwrite
import win32 ""core:sys/windows""
import ""vendor:directx/dxgi""
import ""vendor:directx/d3d11""

LOGFONTA :: struct {} // Use the LOGFONTW functions instead.
FONTSIGNATURE :: struct {
  Usb: [4]win32.DWORD,
  Csv: [4]win32.DWORD,
}

IWICBitmapSource :: struct { #subtype parent: win32.IUnknown }
IWICBitmap :: struct { #subtype parent: win32.IUnknown }
IWICColorContext :: struct { #subtype parent: win32.IUnknown }
IWICImagingFactory :: struct { #subtype parent: win32.IUnknown }
IPrintDocumentPackageTarget :: struct { #subtype parent: win32.IUnknown }");

            foreach (var importLib in importLibs) {
                file.WriteLine();
                file.WriteLine($"foreign import \"system:{importLib.libName}.lib\"");
                file.WriteLine($"@(default_calling_convention=\"system\")");
                file.WriteLine($"foreign {importLib.libName} {{");
                foreach (var function in functions.OrderBy(x => x.Name)) {
                    if (function.Name.StartsWith(importLib.prefix)) {
                        file.Write($"\t{function.Name} :: proc(");
                        file.Write(string.Join(", ", function.Parameters.Select(p => $"{SanitizeVariableName(p.Name)}: {GetSimpleType(p.ParameterType)}")));
                        file.Write(")");
                        var returnType = GetSimpleType(function.ReturnType);
                        if (returnType != "") {
                            file.Write($" -> {returnType}");
                        }
                        file.WriteLine(" ---");
                    }
                }
                file.WriteLine("}");
            }

            file.WriteLine();
            foreach (var field in fields.OrderBy(x => x.Name)) {
                file.Write($"{field.Name}");

                if (field.FieldType.FullName == "System.Guid") {
                    var guidAttr = field.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.GuidAttribute");
                    Debug.Assert(guidAttr != null);
                    var ga = guidAttr.ConstructorArguments;
                    file.WriteLine($" := &win32.IID{{0x{ga[0].Value:x8}, 0x{ga[1].Value:x4}, 0x{ga[2].Value:x4}, {{{string.Join(", ", ga.Skip(3).Select(x => $"0x{x.Value:x2}"))}}}}}");
                } else {
                    Debug.Assert(field.HasConstant);
                    if (field.FieldType.Name == "HRESULT") {
                        file.WriteLine($" :: transmute(win32.HRESULT)u32(0x{(uint)(int)field.Constant:x8})");
                    } else if (field.Constant is int || field.Constant is uint) {
                        file.WriteLine($" :: {field.Constant}");
                    } else if (field.Constant is float) {
                        file.WriteLine($" :: {(float)field.Constant}");
                    } else {
                        throw new NotImplementedException();
                    }
                }
            }

            file.WriteLine();
            foreach (var functionType in functionTypes.OrderBy(x => x.Name)) {
                var method = functionType.Methods.First(m => m.Name == "Invoke");

                file.Write($"{functionType.Name} :: #type proc \"system\" (");
                file.Write(string.Join(", ", method.Parameters.Select(p => $"{SanitizeVariableName(p.Name)}: {GetSimpleType(p.ParameterType)}")));
                file.Write(")");
                var returnType = GetSimpleType(method.ReturnType);
                if (returnType != "") {
                    file.Write($" -> {returnType}");
                }
                file.WriteLine();
            }

            foreach (var _enum in enums.OrderBy(x => x.Name)) {
                file.WriteLine();
                file.WriteLine($"{_enum.Name} :: enum {{");
                foreach (var field in _enum.Fields.Where(field => !field.IsSpecialName)) {
                    var fieldName = field.Name;
                    if (fieldName.StartsWith(_enum.Name + "_")) {
                        fieldName = fieldName.Substring(_enum.Name.Length + 1);
                    }
                    if (Char.IsAsciiDigit(fieldName[0])) {
                        fieldName = "_" + fieldName;
                    }

                    file.WriteLine($"\t{fieldName} = {field.Constant},");
                }
                file.WriteLine("}");
            }

            foreach (var _struct in structs.OrderBy(x => x.Name)) {
                file.WriteLine();
                file.WriteLine($"{_struct.Name} :: struct {{");
                foreach (var field in _struct.Fields) {
                    WriteField(file, 1, field);
                }
                file.WriteLine("}");
            }

            foreach (var _interface in interfaces.OrderBy(x => x.Name)) {
                file.WriteLine();

                var guidAttr = _interface.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.GuidAttribute");
                Debug.Assert(guidAttr != null);
                var ga = guidAttr.ConstructorArguments;
                file.WriteLine($"{_interface.Name}_UUID := &win32.IID{{0x{ga[0].Value:x8}, 0x{ga[1].Value:x4}, 0x{ga[2].Value:x4}, {{{string.Join(", ", ga.Skip(3).Select(x => $"0x{x.Value:x2}"))}}}}}");

                Debug.Assert(_interface.BaseType == null);
                Debug.Assert(_interface.Interfaces.Count == 1);
                var parentType = _interface.Interfaces[0].InterfaceType.Resolve();
                var parentTypeName = ToOdinName(parentType.Name);

                file.WriteLine($"{_interface.Name} :: struct #raw_union {{");
                file.WriteLine($"\t#subtype parent: {parentTypeName},");
                file.WriteLine($"\tusing vtable: ^{_interface.Name}_VTable,");
                file.WriteLine("}");

                var methodNames = new HashSet<string>();

                var parentTypeRecursive = parentType;
                while (true) {
                    foreach (var method in parentTypeRecursive.Methods) {
                        ResolveNameCollisions(method.Name, methodNames);
                    }
                    if (parentTypeRecursive.Interfaces.Count > 0) {
                        Debug.Assert(parentTypeRecursive.Interfaces.Count == 1);
                        parentTypeRecursive = parentTypeRecursive.Interfaces[0].InterfaceType.Resolve();
                    } else {
                        break;
                    }
                }

                file.WriteLine($"{_interface.Name}_VTable :: struct {{");
                var parentVtableName = parentTypeName.Substring(parentTypeName.LastIndexOf(".") + 1).ToLower();
                file.WriteLine($"\tusing {parentVtableName}_vtable: {parentTypeName}_VTable,");
                foreach (var method in _interface.Methods) {
                    var methodName = ResolveNameCollisions(method.Name, methodNames);
                    if (methodName != method.Name) {
                        Console.WriteLine($"Renamed {_interface.Name}.{method.Name} to {methodName}");
                    }
                    file.Write($"\t{methodName}: proc \"system\" (this: ^{_interface.Name}");
                    foreach (var parameter in method.Parameters) {
                        var parameterName = SanitizeVariableName(parameter.Name);
                        var parameterType = GetSimpleType(parameter.ParameterType);
                        file.Write($", {parameterName}: {parameterType}");
                    }

                    string returnType = GetSimpleType(method.ReturnType);
                    if (IsReturnTypeFixNeeded(method.ReturnType)) {
                        Debug.Assert(method.Parameters.Count == 0); // Note (mhs): I'm not sure if this is strictly speaking needed, or if it is more of a sanity check and if it fails then we go and re-check that the codegen for other functions which need this fixup also is right...
                        file.Write($", _return: ^{returnType})");
                        // Technically these functions also pass '_return' as their return value. But we don't need that, so we don't bother specifying a retunr type.
                    } else {
                        file.Write(")");
                        if (returnType != "") {
                            file.Write($" -> {returnType}");
                        }
                    }
                    file.WriteLine(",");
                }
                file.WriteLine("}");
            }
        }
    }
}