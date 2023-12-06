﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using DragonFruit.Data.Requests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Scriban;

namespace DragonFruit.Data.Roslyn
{
    [Generator(LanguageNames.CSharp)]
    public class ApiRequestSourceGenerator : IIncrementalGenerator
    {
        private static readonly Template RequestTemplate;

        private static readonly HashSet<SpecialType> SupportedCollectionTypes = new(new[]
        {
            SpecialType.System_Array,
            SpecialType.System_Collections_IEnumerable,
            SpecialType.System_Collections_Generic_IList_T,
            SpecialType.System_Collections_Generic_ICollection_T,
            SpecialType.System_Collections_Generic_IEnumerable_T,
            SpecialType.System_Collections_Generic_IReadOnlyList_T,
            SpecialType.System_Collections_Generic_IReadOnlyCollection_T,
        });

        static ApiRequestSourceGenerator()
        {
            using var templateStream = typeof(ApiRequestSourceGenerator).Assembly.GetManifestResourceStream("DragonFruit.Data.Roslyn.Templates.ApiRequest.liquid");

            if (templateStream == null)
            {
                throw new NullReferenceException("Could not find template");
            }

            using var reader = new System.IO.StreamReader(templateStream);
            RequestTemplate = Template.ParseLiquid(reader.ReadToEnd());
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var apiRequestDerivedClasses = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax classDecl && classDecl.Modifiers.Any(x => x.IsKind(SyntaxKind.PartialKeyword)),
                transform: (generatorSyntaxContext, _) => GetSemanticTarget(generatorSyntaxContext));

            IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> targets = context.CompilationProvider.Combine(apiRequestDerivedClasses.Collect());
            context.RegisterSourceOutput(targets, static (spc, source) => Execute(source.Item1, source.Item2, spc));
        }

        private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> requestClasses, SourceProductionContext context)
        {
            if (requestClasses.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (var classDeclaration in requestClasses.Distinct())
            {
                var model = compilation.GetSemanticModel(classDeclaration.SyntaxTree, true);
                var classSymbol = model.GetDeclaredSymbol(classDeclaration)!;

                var sourceBuilder = new StringBuilder("// <auto-generated />");
                var parameters = GetRequestSymbolMetadata(compilation, classSymbol, out var formBodyType);

                var parameterInfo = new
                {
                    ClassName = classSymbol.Name,
                    Namespace = classSymbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),

                    FormBodyType = formBodyType,

                    QueryParameters = parameters[ParameterType.Query],
                    HeaderParameters = parameters[ParameterType.Header]

                    // todo add form body content
                };

                sourceBuilder.Append("\n\n");
                sourceBuilder.Append(RequestTemplate.Render(parameterInfo));
                context.AddSource($"{classSymbol.Name}.g.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
            }
        }

        private static ClassDeclarationSyntax GetSemanticTarget(GeneratorSyntaxContext context)
        {
            var model = context.SemanticModel;
            var classDeclaration = (ClassDeclarationSyntax)context.Node;

            var apiRequestSymbol = context.SemanticModel.Compilation.GetTypeByMetadataName(typeof(ApiRequest).FullName);
            var classSymbol = ModelExtensions.GetDeclaredSymbol(model, classDeclaration) as INamedTypeSymbol;

            // ensure the class isn't abstract
            if (classSymbol?.IsAbstract != false)
            {
                return null;
            }

            while (classSymbol != null)
            {
                if (classSymbol.Equals(apiRequestSymbol, SymbolEqualityComparer.Default))
                {
                    return classDeclaration;
                }

                classSymbol = classSymbol.BaseType;
            }

            return null;
        }

        private static IReadOnlyDictionary<ParameterType, IList<RequestSymbolMetadata>> GetRequestSymbolMetadata(Compilation compilation, INamedTypeSymbol symbol, out FormBodyType? formBodyType)
        {
            var symbols = Enum.GetValues(typeof(ParameterType)).Cast<ParameterType>().ToDictionary(x => x, _ => (IList<RequestSymbolMetadata>)new List<RequestSymbolMetadata>());

            // get types used in member processing
            var enumerableParameterAttribute = compilation.GetTypeByMetadataName(typeof(EnumerableOptionsAttribute).FullName);
            var requestParameterAttribute = compilation.GetTypeByMetadataName(typeof(RequestParameterAttribute).FullName);
            var enumParameterAttribute = compilation.GetTypeByMetadataName(typeof(EnumOptionsAttribute).FullName);
            var formBodyTypeAttribute = compilation.GetTypeByMetadataName(typeof(FormBodyTypeAttribute).FullName);

            var enumerableTypeSymbol = compilation.GetTypeByMetadataName(typeof(IEnumerable).FullName);
            var apiRequestBaseType = compilation.GetTypeByMetadataName(typeof(ApiRequest).FullName);

            // track properties already visited
            var depth = 0;
            var currentSymbol = symbol;
            var consumedProperties = new HashSet<string>();

            formBodyType = null;

            do
            {
                // check for body type attribute
                var formBodyInfo = currentSymbol.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(formBodyTypeAttribute, SymbolEqualityComparer.Default) == true);

                if (formBodyInfo != null)
                {
                    formBodyType = (FormBodyType)formBodyInfo.ConstructorArguments[0].Value!;
                }

                // locate and add symbol metadata
                foreach (var candidate in currentSymbol.GetMembers().Where(x => x is IPropertySymbol or IMethodSymbol { Parameters.Length: 0 }))
                {
                    var requestAttribute = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(requestParameterAttribute, SymbolEqualityComparer.Default) == true);

                    // ensure properties overwritten using "new" are not processed twice
                    if (requestAttribute == null || !consumedProperties.Add(candidate.MetadataName))
                    {
                        continue;
                    }

                    // only allow public, protected and protected internal properties
                    if (candidate.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal)
                    {
                        // todo diagnostic warning
                        continue;
                    }

                    var returnType = candidate switch
                    {
                        IPropertySymbol propertySymbol => propertySymbol.Type,
                        IMethodSymbol methodSymbol => methodSymbol.ReturnType,

                        _ => throw new NotSupportedException()
                    };

                    // if a method is declared, ensure it's not void
                    if (returnType.SpecialType == SpecialType.System_Void)
                    {
                        // todo return diagnostic warning
                        continue;
                    }

                    var parameterType = (ParameterType)requestAttribute.ConstructorArguments[0].Value!;
                    var parameterName = (string)requestAttribute.ConstructorArguments.ElementAtOrDefault(1).Value ?? candidate.Name;

                    RequestSymbolMetadata metadata;

                    // handle enums
                    if (returnType.TypeKind == TypeKind.Enum)
                    {
                        var enumOptions = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(enumParameterAttribute, SymbolEqualityComparer.Default) == true);
                        var enumType = (EnumOption?)enumOptions?.ConstructorArguments.ElementAt(0).Value ?? EnumOption.None;

                        metadata = new EnumRequestSymbolMetadata
                        {
                            EnumOption = enumType.ToString()
                        };
                    }
                    // handle arrays/IEnumerable
                    else if (SupportedCollectionTypes.Contains(returnType.SpecialType) || returnType.FindImplementationForInterfaceMember(enumerableTypeSymbol) != null)
                    {
                        var enumerableOptions = candidate.GetAttributes().SingleOrDefault(x => x.AttributeClass?.Equals(enumerableParameterAttribute, SymbolEqualityComparer.Default) == true);
                        var enumerableType = (EnumerableOption?)enumerableOptions?.ConstructorArguments.ElementAt(0).Value ?? EnumerableOption.Concatenated;

                        metadata = new EnumerableRequestSymbolMetadata
                        {
                            Separator = (string)enumerableOptions?.ConstructorArguments.ElementAtOrDefault(1).Value ?? ",",
                            EnumerableOption = enumerableType.ToString()
                        };
                    }
                    else
                    {
                        metadata = new RequestSymbolMetadata
                        {
                            IsString = returnType.SpecialType == SpecialType.System_String
                        };
                    }

                    metadata.Depth = depth;
                    metadata.Symbol = candidate;
                    metadata.ParameterName = parameterName;
                    metadata.Nullable = returnType.IsReferenceType || (returnType.IsValueType && returnType.NullableAnnotation == NullableAnnotation.Annotated);

                    symbols[parameterType].Add(metadata);
                }

                // get derived class from currentSymbol
                currentSymbol = currentSymbol.BaseType;
                depth++;
            } while (currentSymbol?.Equals(apiRequestBaseType, SymbolEqualityComparer.Default) == false);

            return symbols;
        }
    }
}
