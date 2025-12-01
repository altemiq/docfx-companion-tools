using System.CommandLine;
using Humanizer;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

var specSourceOption = new Option<FileSystemInfo>("-s", "--specsource")
    { Description = "Folder or File containing the OpenAPI specification.", Required = true }.AcceptExistingOnly();
var outputFolderOption = new Option<DirectoryInfo>("-o", "--outputfolder")
    { Description = "Folder to write the resulting specifications in." };
var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show verbose messages." };
var generateOperationIdOption = new Option<bool>("-g", "--genOpId") { Description = "Generate missing OperationId members." };

var rootCommand = new RootCommand
{
    specSourceOption,
    outputFolderOption,
    verboseOption,
    generateOperationIdOption,
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var specSource = parseResult.GetRequiredValue(specSourceOption);
    var outputFolder = parseResult.GetValue(outputFolderOption) ?? GetDirectoryInfo(specSource);
    var verbose = parseResult.GetValue(verboseOption);
    var generateOperationId = parseResult.GetValue(generateOperationIdOption);

    if (verbose)
    {
        await parseResult.InvocationConfiguration.Output
            .WriteLineAsync($"Specification file/folder: {specSource}")
            .ConfigureAwait(false);
        await parseResult.InvocationConfiguration.Output
            .WriteLineAsync($"Output folder       : {outputFolder}")
            .ConfigureAwait(false);
        await parseResult.InvocationConfiguration.Output
            .WriteLineAsync($"Verbose             : {verbose}")
            .ConfigureAwait(false);
        await parseResult.InvocationConfiguration.Output
            .WriteLineAsync($"Generate OperationId Members: {generateOperationId}")
            .ConfigureAwait(false);
    }

    if (!specSource.Exists)
    {
        await parseResult.InvocationConfiguration.Error
            .WriteLineAsync($"ERROR: Specification folder/file '{specSource}' doesn't exist.")
            .ConfigureAwait(false);
        return 1;
    }

    if (outputFolder is null)
    {
        await parseResult.InvocationConfiguration.Error
            .WriteLineAsync("ERROR: Output folder is not specified.")
            .ConfigureAwait(false);
        return 1;
    }

    outputFolder.Create();

    return await ConvertOpenApiSourceAsync();

    static DirectoryInfo? GetDirectoryInfo(FileSystemInfo source)
    {
        return source switch
        {
            DirectoryInfo directory => directory,
            FileInfo file => file.Directory,
            _ => null,
        };
    }

    async Task<int> ConvertOpenApiSourceAsync()
    {
        switch (specSource)
        {
            case FileInfo { Exists: true } fileInfo:
                return await ConvertOpenApiFileAsync(fileInfo);
            case DirectoryInfo { Exists: true } directoryInfo:
            {
                IEnumerable<string> openApiFileExtensions = ["*.json", "*.yaml", "*.yml"];
                foreach (var fileInfo in openApiFileExtensions
                             .SelectMany(extension => directoryInfo.EnumerateFiles(
                                 extension,
                                 new EnumerationOptions
                                 {
                                     MatchCasing = MatchCasing.CaseInsensitive,
                                     RecurseSubdirectories = true,
                                 })))
                {
                    if (await ConvertOpenApiFileAsync(fileInfo).ConfigureAwait(false) is not 0 and var returnValue)
                    {
                        return returnValue;
                    }
                }

                return 0;
            }
            default:
                return 2;
        }
    }

    async Task<int> ConvertOpenApiFileAsync(FileInfo input)
    {
        if (verbose)
        {
            await parseResult.InvocationConfiguration.Output
                .WriteLineAsync($"Reading OpenAPI file '{input}'")
                .ConfigureAwait(false);
        }

        ReadResult result;
        var stream = input.OpenRead();
        await using (stream)
        {
            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();

            result = await OpenApiDocument
                .LoadAsync(stream, settings: settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.Diagnostic is { Errors: { Count: not 0 } errors })
        {
            await parseResult.InvocationConfiguration.Error
                .WriteLineAsync("ERROR: Not a valid OpenAPI v2 or v3 specification")
                .ConfigureAwait(false);
            foreach (var error in errors)
            {
                await parseResult.InvocationConfiguration.Error
                    .WriteLineAsync(error.ToString())
                    .ConfigureAwait(false);
            }

            return 1;
        }

        if (result is not { Diagnostic: { } diagnostic, Document: { } document })
        {
            return 1;
        }

        if (verbose)
        {
            await parseResult.InvocationConfiguration.Output
                .WriteLineAsync($"Input OpenAPI version '{diagnostic.SpecificationVersion}'")
                .ConfigureAwait(false);
        }

        foreach (var (pathName, path) in document.Paths)
        {
            foreach (var (operationType, operation) in CreateNullSafe(path.Operations))
            {
                if (generateOperationId)
                {
                    operation.OperationId ??= GenerateOperationId(operationType, pathName, operation.Parameters);
                }

                var description = $"{pathName} {operationType}";

                foreach (var (responseType, response) in CreateNullSafe(operation.Responses))
                {
                    foreach (var (mediaType, content) in CreateNullSafe(response.Content))
                    {
                        await CreateSingleExampleFromMultipleExamplesAsync(
                                content,
                                $"{description} response {responseType} {mediaType}")
                            .ConfigureAwait(false);
                    }
                }

                foreach (var parameter in CreateNullSafe(operation.Parameters))
                {
                    foreach (var (mediaType, content) in CreateNullSafe(parameter.Content))
                    {
                        await CreateSingleExampleFromMultipleExamplesAsync(
                                content,
                                $"{description} parameter {parameter.Name} {mediaType}")
                            .ConfigureAwait(false);
                    }
                }

                foreach (var (mediaType, content) in CreateNullSafe(operation.RequestBody?.Content))
                {
                    await CreateSingleExampleFromMultipleExamplesAsync(
                            content,
                            $"{description} requestBody {mediaType}")
                        .ConfigureAwait(false);

                    if (content is not OpenApiMediaType
                        {
                            Example: { } example, Schema: OpenApiSchema { Example: null } schema,
                        })
                    {
                        continue;
                    }

                    if (verbose)
                    {
                        await parseResult.InvocationConfiguration.Output
                            .WriteLineAsync(
                                $"[OpenAPIv2 compatibility] Setting type example from sample requestBody example for {content.Schema?.Schema?.ToString() ?? "item"} from {operation.OperationId}")
                            .ConfigureAwait(false);
                    }

                    schema.Example = example;
                }
            }

            static IEnumerable<T> CreateNullSafe<T>(IEnumerable<T>? enumerable)
            {
                return enumerable ?? [];
            }
        }

        var outputFile = Path.Combine(outputFolder.FullName, Path.ChangeExtension(input.Name, ".swagger.json"));
        if (verbose)
        {
            await parseResult.InvocationConfiguration.Output
                .WriteLineAsync($"Writing output file '{outputFile}' as version '{OpenApiSpecVersion.OpenApi2_0}'")
                .ConfigureAwait(false);
        }

        var outputStream = File.Create(outputFile);
        await using (outputStream)
        {
            var textWriter = new StreamWriter(outputStream);
            await using (textWriter)
            {
                var settings = new OpenApiJsonWriterSettings();
                document.SerializeAsV2(new OpenApiJsonWriter(textWriter, settings));
            }
        }

        return 0;

        async Task CreateSingleExampleFromMultipleExamplesAsync(IOpenApiMediaType content, string description)
        {
            if (content is OpenApiMediaType { Example: null, Examples: { Count: not 0 } examples } mediaType)
            {
                if (verbose)
                {
                    await parseResult.InvocationConfiguration.Output
                        .WriteLineAsync(
                            $"[OpenAPIv2 compatibility] Setting example from first of multiple OpenAPIv3 examples for {description}")
                        .ConfigureAwait(false);
                }

                mediaType.Example = examples.Values.First().Value;
            }
        }

        string GenerateOperationId(HttpMethod operationType, string pathName, IList<IOpenApiParameter>? parameters)
        {
            return string.Join(string.Empty, SplitPathString(operationType, pathName, parameters));

            static IEnumerable<string> SplitPathString(HttpMethod operationType, string path,
                IList<IOpenApiParameter>? parameters)
            {
                yield return operationType.ToString().ToLowerInvariant();

                var start = path.StartsWith("/api/", StringComparison.InvariantCultureIgnoreCase)
                    ? path[5..]
                    : path;

                foreach (var split in start.Split(
                             '/',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (split.StartsWith('{'))
                    {
                        break;
                    }

                    yield return split.Pascalize();
                }

                if (parameters is null or { Count: 0 })
                {
                    yield break;
                }

                yield return "By";

                foreach (var parameter in parameters
                             .Where(it => it is { In: ParameterLocation.Path, Name: not null })
                             .Select(it => it.Name!.Pascalize()))
                {
                    yield return parameter;
                }
            }
        }
    }
});

await rootCommand
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);