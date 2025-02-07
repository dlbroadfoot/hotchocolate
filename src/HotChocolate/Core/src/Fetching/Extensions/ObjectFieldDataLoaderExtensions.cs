using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using GreenDonut;
using HotChocolate.Internal;
using HotChocolate.Resolvers;
using HotChocolate.Types.Descriptors;
using HotChocolate.Types.Descriptors.Definitions;
using static HotChocolate.Fetching.Utilities.ThrowHelper;
using static HotChocolate.WellKnownMiddleware;

#nullable enable

namespace HotChocolate.Types;

public static class DataLoaderObjectFieldExtensions
{
    public static IObjectFieldDescriptor UseDataloader<TDataLoader>(
        this IObjectFieldDescriptor descriptor)
        where TDataLoader : IDataLoader
        => UseDataloader(descriptor, typeof(TDataLoader));

    public static IObjectFieldDescriptor UseDataloader(
        this IObjectFieldDescriptor descriptor,
        Type dataLoaderType)
    {
        FieldMiddlewareDefinition placeholder = new(_ => _ => default, key: DataLoader);

        if (!TryGetDataLoaderTypes(dataLoaderType, out Type? keyType, out Type? valueType))
        {
            throw DataLoader_InvalidType(dataLoaderType);
        }

        descriptor.Extend().Definition.MiddlewareDefinitions.Add(placeholder);

        descriptor
            .Extend()
            .OnBeforeCreate(
                (c, definition) =>
                {
                    IExtendedType schemaType;
                    if (!valueType.IsArray)
                    {
                        IExtendedType resolverType =
                            c.TypeInspector.GetType(definition.ResultType!);

                        schemaType = c.TypeInspector.GetType(resolverType.IsArrayOrList
                            ? typeof(IEnumerable<>).MakeGenericType(valueType)
                            : valueType);
                    }
                    else
                    {
                        schemaType = c.TypeInspector.GetType(valueType);
                    }

                    definition.Type = TypeReference.Create(schemaType, TypeContext.Output);
                    definition.Configurations.Add(
                        new CompleteConfiguration<ObjectFieldDefinition>(
                            (_, def) =>
                            {
                                CompileMiddleware(
                                    def,
                                    placeholder,
                                    keyType,
                                    valueType,
                                    dataLoaderType);
                            },
                            definition,
                            ApplyConfigurationOn.BeforeCompletion));
                });

        return descriptor;
    }

    private static void CompileMiddleware(
        ObjectFieldDefinition definition,
        FieldMiddlewareDefinition placeholder,
        Type keyType,
        Type valueType,
        Type dataLoaderType)
    {
        Type middlewareType;
        if (valueType.IsArray)
        {
            middlewareType =
                typeof(GroupedDataLoaderMiddleware<,,>)
                    .MakeGenericType(dataLoaderType, keyType, valueType.GetElementType()!);
        }
        else
        {
            middlewareType =
                typeof(DataLoaderMiddleware<,,>)
                    .MakeGenericType(dataLoaderType, keyType, valueType);
        }

        FieldMiddleware middleware = FieldClassMiddlewareFactory.Create(middlewareType);
        var index = definition.MiddlewareDefinitions.IndexOf(placeholder);
        definition.MiddlewareDefinitions[index] = new(middleware, key: DataLoader);
    }

    private static bool TryGetDataLoaderTypes(
        Type type,
        [NotNullWhen(true)] out Type? key,
        [NotNullWhen(true)] out Type? value)
    {
        foreach (Type interfaceType in type.GetInterfaces())
        {
            if (interfaceType.IsGenericType)
            {
                Type typeDefinition = interfaceType.GetGenericTypeDefinition();
                if (typeof(IDataLoader<,>) == typeDefinition)
                {
                    key = interfaceType.GetGenericArguments()[0];
                    value = interfaceType.GetGenericArguments()[1];
                    return true;
                }
            }
        }

        key = null;
        value = null;
        return false;
    }

    private sealed class GroupedDataLoaderMiddleware<TDataLoader, TKey, TValue>
        where TKey : notnull
        where TDataLoader : IDataLoader<TKey, TValue[]>
    {
        private readonly FieldDelegate _next;

        public GroupedDataLoaderMiddleware(FieldDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            TDataLoader dataloader = context.DataLoader<TDataLoader>();

            await _next(context).ConfigureAwait(false);

            if (context.Result is IReadOnlyCollection<TKey> values)
            {
                IReadOnlyList<TValue[]> data = await dataloader
                    .LoadAsync(values, context.RequestAborted)
                    .ConfigureAwait(false);

                var result = new HashSet<object>();
                for (var m = 0; m < data.Count; m++)
                {
                    for (var n = 0; n < data[m].Length; n++)
                    {
                        result.Add(data[m][n]!);
                    }
                }

                context.Result = result;
            }
            else if (context.Result is TKey value)
            {
                context.Result = await dataloader
                    .LoadAsync(value, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed class DataLoaderMiddleware<TDataLoader, TKey, TValue>
        where TKey : notnull
        where TDataLoader : IDataLoader<TKey, TValue>
    {
        private readonly FieldDelegate _next;

        public DataLoaderMiddleware(FieldDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            TDataLoader dataloader = context.DataLoader<TDataLoader>();

            await _next(context).ConfigureAwait(false);

            if (context.Result is IReadOnlyCollection<TKey> values)
            {
                context.Result = await dataloader
                    .LoadAsync(values, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            else if (context.Result is TKey value)
            {
                context.Result = await dataloader
                    .LoadAsync(value, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }
}
