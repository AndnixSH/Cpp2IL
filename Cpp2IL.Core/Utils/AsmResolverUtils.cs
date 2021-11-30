using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Utils
{
    public static class AsmResolverUtils
    {
        private static readonly object PointerReadLock = new();
        private static readonly Dictionary<string, (TypeDefinition typeDefinition, string[] genericParams)?> CachedTypeDefsByName = new();
        private static readonly Dictionary<AssemblyDefinition, ReferenceImporter> ImportersByAssembly = new();

        public static ITypeDescriptor GetTypeDefFromIl2CppType(ReferenceImporter importer, Il2CppType il2CppType)
        {
            var theDll = LibCpp2IlMain.Binary!;

            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Object.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Void.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Boolean.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Char.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                    return importer.ImportType(TypeDefinitionsAsmResolver.SByte.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Byte.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Int16.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return importer.ImportType(TypeDefinitionsAsmResolver.UInt16.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Int32.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                    return importer.ImportType(TypeDefinitionsAsmResolver.UInt32.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                    return importer.ImportType(TypeDefinitionsAsmResolver.IntPtr.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return importer.ImportType(TypeDefinitionsAsmResolver.UIntPtr.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Int64.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                    return importer.ImportType(TypeDefinitionsAsmResolver.UInt64.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Single.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Double.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                    return importer.ImportType(TypeDefinitionsAsmResolver.String.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                    return importer.ImportType(TypeDefinitionsAsmResolver.TypedReference.ToTypeReference());
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                {
                    var typeDefinition = SharedState.TypeDefsByIndexNew[il2CppType.data.classIndex];
                    return importer.ImportType(typeDefinition.ToTypeReference());
                }

                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                {
                    var arrayType = theDll.ReadClassAtVirtualAddress<Il2CppArrayType>(il2CppType.data.array);
                    var oriType = theDll.GetIl2CppTypeFromPointer(arrayType.etype);
                    return importer.ImportTypeIfNeeded(GetTypeDefFromIl2CppType(importer, oriType).ToTypeDefOrRef()).MakeArrayType(arrayType.rank);
                }

                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                {
                    var genericClass =
                        theDll.ReadClassAtVirtualAddress<Il2CppGenericClass>(il2CppType.data.generic_class);
                    TypeDefinition typeDefinition;
                    if (LibCpp2IlMain.MetadataVersion < 27f)
                    {
                        typeDefinition = SharedState.TypeDefsByIndexNew[genericClass.typeDefinitionIndex];
                    }
                    else
                    {
                        //V27 - type indexes are pointers now.
                        var type = theDll.ReadClassAtVirtualAddress<Il2CppType>((ulong)genericClass.typeDefinitionIndex);
                        type.Init();
                        typeDefinition = GetTypeDefFromIl2CppType(importer, type).Resolve() ?? throw new Exception("Unable to resolve base type for generic inst");
                    }
                    
                    var genericInstanceType = new GenericInstanceTypeSignature(importer.ImportType(typeDefinition.ToTypeReference()), typeDefinition.IsValueType);
                    var genericInst = theDll.ReadClassAtVirtualAddress<Il2CppGenericInst>(genericClass.context.class_inst);
                    ulong[] pointers;

                    lock (PointerReadLock)
                        pointers = theDll.GetPointers(genericInst.pointerStart, (long)genericInst.pointerCount);

                    foreach (var pointer in pointers)
                    {
                        var oriType = theDll.GetIl2CppTypeFromPointer(pointer);
                        if (oriType.type is not Il2CppTypeEnum.IL2CPP_TYPE_VAR and not Il2CppTypeEnum.IL2CPP_TYPE_MVAR)
                            genericInstanceType.TypeArguments.Add(importer.ImportTypeSignature(GetTypeDefFromIl2CppType(importer, oriType).MakeTypeSignature()));
                        else
                        {
                            var gpt = oriType.type is Il2CppTypeEnum.IL2CPP_TYPE_MVAR ? GenericParameterType.Method : GenericParameterType.Type;
                            var gp = LibCpp2IlMain.TheMetadata!.genericParameters[oriType.data.genericParameterIndex];
                            genericInstanceType.TypeArguments.Add(new GenericParameterSignature(gpt, gp.genericParameterIndexInOwner));
                        }
                    }

                    return genericInstanceType;
                }

                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                {
                    var oriType = theDll.GetIl2CppTypeFromPointer(il2CppType.data.type);
                    if (oriType.type is not Il2CppTypeEnum.IL2CPP_TYPE_MVAR and not Il2CppTypeEnum.IL2CPP_TYPE_VAR)
                        return importer.ImportTypeIfNeeded(GetTypeDefFromIl2CppType(importer, oriType).ToTypeDefOrRef()).MakeSzArrayType();

                    var gp = LibCpp2IlMain.TheMetadata!.genericParameters[oriType.data.genericParameterIndex];
                    var gpt = oriType.type is Il2CppTypeEnum.IL2CPP_TYPE_MVAR ? GenericParameterType.Method : GenericParameterType.Type;
                    return new GenericParameterSignature(gpt, gp.genericParameterIndexInOwner).MakeSzArrayType();
                }

                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                {
                    var gp = LibCpp2IlMain.TheMetadata!.genericParameters[il2CppType.data.genericParameterIndex];
                    var gpt = il2CppType.type is Il2CppTypeEnum.IL2CPP_TYPE_MVAR ? GenericParameterType.Method : GenericParameterType.Type;
                    return new GenericParameterSignature(importer.TargetModule, gpt, gp.genericParameterIndexInOwner);
                }
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                {
                    var oriType = theDll.GetIl2CppTypeFromPointer(il2CppType.data.type);
                    return importer.ImportType(GetTypeDefFromIl2CppType(importer, oriType).ToTypeDefOrRef()).MakePointerType();
                }

                default:
                    return importer.ImportType(TypeDefinitionsAsmResolver.Object.ToTypeReference());
            }
        }

        public static GenericParameter ImportGenericParameter(Il2CppType il2CppType, ReferenceImporter importer, bool isMethod)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                {
                    if (SharedState.GenericParamsByIndexNew.TryGetValue(il2CppType.data.genericParameterIndex, out var genericParameter))
                        return genericParameter;

                    var param = LibCpp2IlMain.TheMetadata!.genericParameters[il2CppType.data.genericParameterIndex];
                    var genericName = LibCpp2IlMain.TheMetadata.GetStringFromIndex(param.nameIndex);
                    if (isMethod)
                    {
                        genericParameter = new GenericParameter(genericName, (GenericParameterAttributes) param.flags);

                        SharedState.GenericParamsByIndexNew.Add(il2CppType.data.genericParameterIndex, genericParameter);

                        param.ConstraintTypes!
                            .Select(c => new GenericParameterConstraint(importer.ImportTypeIfNeeded(GetTypeDefFromIl2CppType(importer, c).ToTypeDefOrRef())))
                            .ToList()
                            .ForEach(genericParameter.Constraints.Add);

                        return genericParameter;
                    }

                    genericParameter = new GenericParameter(genericName, (GenericParameterAttributes) param.flags);

                    SharedState.GenericParamsByIndexNew.Add(il2CppType.data.genericParameterIndex, genericParameter);
                    
                    param.ConstraintTypes!
                        .Select(c => new GenericParameterConstraint(importer.ImportTypeIfNeeded(GetTypeDefFromIl2CppType(importer, c).ToTypeDefOrRef())))
                        .ToList()
                        .ForEach(genericParameter.Constraints.Add);

                    return genericParameter;
                }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                {
                    if (SharedState.GenericParamsByIndexNew.TryGetValue(il2CppType.data.genericParameterIndex, out var genericParameter))
                    {
                        return genericParameter;
                    }

                    var param = LibCpp2IlMain.TheMetadata!.genericParameters[il2CppType.data.genericParameterIndex];
                    var genericName = LibCpp2IlMain.TheMetadata.GetStringFromIndex(param.nameIndex);
                    genericParameter = new GenericParameter(genericName, (GenericParameterAttributes) param.flags);

                    SharedState.GenericParamsByIndexNew.Add(il2CppType.data.genericParameterIndex, genericParameter);

                    param.ConstraintTypes!
                        .Select(c => new GenericParameterConstraint(importer.ImportTypeIfNeeded(GetTypeDefFromIl2CppType(importer, c).ToTypeDefOrRef())))
                        .ToList()
                        .ForEach(genericParameter.Constraints.Add);

                    return genericParameter;
                }
                default:
                    throw new($"Can't import {il2CppType.type} as a generic param because it isn't one");
            }
        }

        private static TypeSignature MakeTypeSignature(this ITypeDescriptor raw)
        {
            return raw switch
            {
                TypeSignature sig => sig,
                TypeDefinition def => new TypeDefOrRefSignature(def.ToTypeReference()),
                ITypeDefOrRef typeDefOrRef => new TypeDefOrRefSignature(typeDefOrRef),
                _ => throw new($"Can't make a type signature for {raw} of type {raw.GetType()}")
            };
        }
        
        public static TypeDefinition? TryLookupTypeDefKnownNotGeneric(string? name) => TryLookupTypeDefByName(name)?.typeDefinition;
        
        public static (TypeDefinition typeDefinition, string[] genericParams)? TryLookupTypeDefByName(string? name)
        {
            if (name == null) 
                return null;

            var key = name.ToLower(CultureInfo.InvariantCulture);

            if (CachedTypeDefsByName.TryGetValue(key, out var ret))
                return ret;

            var result = InternalTryLookupTypeDefByName(name);

            CachedTypeDefsByName[key] = result;

            return result;
        }
        
        private static (TypeDefinition typeDefinition, string[] genericParams)? InternalTryLookupTypeDefByName(string name)
        {
            if (TypeDefinitionsAsmResolver.GetPrimitive(name) is {} primitive)
                return new (primitive, Array.Empty<string>());
            
            //The only real cases we end up here are:
            //From explicit override resolving, because that has to be done by name
            //Sometimes in attribute restoration if we come across an object parameter, but this almost always has to be a system or cecil type, or an enum.
            //While originally initializing the TypeDefinitions class, which is always a system type
            //And during exception helper location, which is always a system type.
            //So really the only remapping we should have to handle is during explicit override restoration.

            var definedType = SharedState.AllTypeDefinitionsNew.Find(t => string.Equals(t?.FullName, name, StringComparison.OrdinalIgnoreCase));

            if (name.EndsWith("[]"))
            {
                var without = name[..^2];
                var result = TryLookupTypeDefByName(without);
                return result;
            }

            //Generics are dumb.
            var genericParams = Array.Empty<string>();
            if (definedType == null && name.Contains("<"))
            {
                //Replace < > with the number of generic params after a `
                genericParams = MiscUtils.GetGenericParams(name[(name.IndexOf("<", StringComparison.Ordinal) + 1)..^1]);
                name = name[..name.IndexOf("<", StringComparison.Ordinal)];
                if (!name.Contains("`"))
                    name = name + "`" + (genericParams.Length);

                definedType = SharedState.AllTypeDefinitionsNew.Find(t => t.FullName == name);
            }

            if (definedType != null) return (definedType, genericParams);

            //It's possible they didn't specify a `System.` prefix
            var searchString = $"System.{name}";
            definedType = SharedState.AllTypeDefinitionsNew.Find(t => string.Equals(t.FullName, searchString, StringComparison.OrdinalIgnoreCase));

            if (definedType != null) return (definedType, genericParams);

            //Still not got one? Ok, is there only one match for non FQN?
            var matches = SharedState.AllTypeDefinitionsNew.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
                definedType = matches.First();

            if (definedType != null) 
                return (definedType, genericParams);

            if (!name.Contains("."))
                return null;

            searchString = name;
            //Try subclasses
            matches = SharedState.AllTypeDefinitionsNew.Where(t => t.FullName.Replace('/', '.').EndsWith(searchString)).ToList();
            if (matches.Count == 1)
                definedType = matches.First();

            if (definedType == null)
                return null;

            return new (definedType, genericParams);
        }

        public static ReferenceImporter GetImporter(this AssemblyDefinition assemblyDefinition)
        {
            if (ImportersByAssembly.TryGetValue(assemblyDefinition, out var ret))
                return ret;

            ImportersByAssembly[assemblyDefinition] = ret = new(assemblyDefinition.Modules[0]);

            return ret;
        }

        public static TypeSignature ImportTypeSignatureIfNeeded(this ReferenceImporter importer, TypeSignature signature) => signature is GenericParameterSignature ? signature : importer.ImportTypeSignature(signature);

        public static ITypeDefOrRef ImportTypeIfNeeded(this ReferenceImporter importer, ITypeDefOrRef type)
        {
            if (type is TypeSpecification spec)
                return new TypeSpecification(importer.ImportTypeSignatureIfNeeded(spec.Signature!));
            
            return importer.ImportType(type);
        }

        public static ElementType GetElementTypeFromConstant(object? primitive) => primitive is null
            ? ElementType.Object
            : primitive switch
            {
                sbyte => ElementType.I1,
                byte => ElementType.U1,
                bool => ElementType.Boolean,
                short => ElementType.I2,
                ushort => ElementType.U2,
                int => ElementType.I4,
                uint => ElementType.U4,
                long => ElementType.I8,
                ulong => ElementType.U8,
                float => ElementType.R4,
                double => ElementType.R8,
                string => ElementType.String,
                char => ElementType.Char,
                _ => throw new($"Can't get a element type for the constant {primitive} of type {primitive.GetType()}"),
            };

        public static Constant MakeConstant(object from)
        {
            if (from is string s)
                return new(ElementType.String, new(Encoding.Unicode.GetBytes(s)));
            
            return new(GetElementTypeFromConstant(from), new DataBlobSignature(MiscUtils.RawBytes((IConvertible) from)));
        }

        public static bool IsManagedMethodWithBody(this MethodDefinition managedMethod) => 
            managedMethod.Managed && !managedMethod.IsAbstract && !managedMethod.IsPInvokeImpl 
            && !managedMethod.IsInternalCall && !managedMethod.IsNative && !managedMethod.IsRuntime;
    }
}