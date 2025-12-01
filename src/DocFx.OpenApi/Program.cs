using System.CommandLine;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

var specSourceOption = new Option<string>("-s", "--specsource")
    { Description = "Folder or File containing the OpenAPI specification.", Required = true };
var outputFolderOption = new Option<string>("-o", "--outputfolder")
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
    string[] openApiFileExtensions = ["json", "yaml", "yml"];

    var specSource = parseResult.GetRequiredValue(specSourceOption);
    var outputFolder = parseResult.GetValue(outputFolderOption) ?? specSource;
    var verbose = parseResult.GetValue(verboseOption);
    var generateOperationId = parseResult.GetValue(generateOperationIdOption);

    if (verbose)
    {
        await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Specification file/folder: {specSource}")
            .ConfigureAwait(false);
        await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Output folder       : {outputFolder}");
        await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Verbose             : {verbose}");
        await parseResult.InvocationConfiguration.Output.WriteLineAsync(
            $"Generate OperationId Members: {generateOperationId}");
    }

    if (!Path.Exists(specSource))
    {
        await parseResult.InvocationConfiguration.Error.WriteLineAsync(
            $"ERROR: Specification folder/file '{specSource}' doesn't exist.");
        return 1;
    }

    Directory.CreateDirectory(outputFolder);

    return await ConvertOpenApiSourceAsync();
    
    async Task<int> ConvertOpenApiSourceAsync()
    {
        if (File.Exists(specSource))
        {
            return await ConvertOpenApiFileAsync(specSource);
        }

        foreach (var extension in openApiFileExtensions)
        {
            if (await ConvertOpenApiExtensionAsync(extension) is not 0 and var returnValue)
            {
                return returnValue;
            }
        }

        return 0;
    }
    
    async Task<int> ConvertOpenApiExtensionAsync(string extension)
    {
        foreach (var file in Directory.GetFiles(
                     specSource,
                     $"*.{extension}",
                     new EnumerationOptions
                     {
                         MatchCasing = MatchCasing.CaseInsensitive,
                         RecurseSubdirectories = true,
                     }))
        {
            if (await ConvertOpenApiFileAsync(file) is not 0 and var returnValue)
            {
                return returnValue;
            }
        }

        return 0;
    }

    async Task<int> ConvertOpenApiFileAsync(string inputSpecFile)
    {
        if (verbose)
        {
            await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Reading OpenAPI file '{inputSpecFile}'")
                .ConfigureAwait(false);
        }

        ReadResult result;
        var stream = File.OpenRead(inputSpecFile);
        await using (stream)
        {
            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();

            result = await OpenApiDocument.LoadAsync(stream, settings: settings, cancellationToken: cancellationToken);
        }

        if (result.Diagnostic is { Errors: { Count: not 0 } errors })
        {
            await parseResult.InvocationConfiguration.Error.WriteLineAsync("ERROR: Not a valid OpenAPI v2 or v3 specification")
                .ConfigureAwait(false);
            foreach (var error in errors)
            {
                await parseResult.InvocationConfiguration.Error.WriteLineAsync(error.ToString())
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
            await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Input OpenAPI version '{diagnostic.SpecificationVersion}'");
        }

        foreach (var (pathName, path) in document.Paths)
        {
            foreach (var (operationType, operation) in CreateNullSafe(path.Operations))
            {
                if (generateOperationId && string.IsNullOrWhiteSpace(operation.OperationId))
                {
                    var operationId = GenerateOperationId(operationType, pathName, operation.Parameters);
                    operation.OperationId = operationId;
                }

                var description = $"{pathName} {operationType}";

                foreach (var (responseType, response) in CreateNullSafe(operation.Responses))
                {
                    foreach (var (mediaType, content) in CreateNullSafe(response.Content))
                    {
                        await CreateSingleExampleFromMultipleExamples(content,
                            $"{description} response {responseType} {mediaType}");
                    }
                }

                foreach (var parameter in CreateNullSafe(operation.Parameters))
                {
                    foreach (var (mediaType, content) in CreateNullSafe(parameter.Content))
                    {
                        await CreateSingleExampleFromMultipleExamples(content,
                            $"{description} parameter {parameter.Name} {mediaType}");
                    }
                }

                foreach (var (mediaType, content) in CreateNullSafe(operation.RequestBody?.Content))
                {
                    await CreateSingleExampleFromMultipleExamples(content, $"{description} requestBody {mediaType}");

                    if (content is not OpenApiMediaType
                        {
                            Example: { } example, Schema: OpenApiSchema { Example: null } schema,
                        })
                    {
                        continue;
                    }
                    
                    if (verbose)
                    {
                        await parseResult.InvocationConfiguration.Output.WriteLineAsync(
                            $"[OpenAPIv2 compatibility] Setting type example from sample requestBody example for {content.Schema?.Schema?.ToString() ?? "item"} from {operation.OperationId}");
                    }

                    schema.Example = example;
                }
            }

            static IEnumerable<T> CreateNullSafe<T>(IEnumerable<T>? enumerable)
            {
                return enumerable ?? [];
            }
        }

        var outputFileName = Path.ChangeExtension(Path.GetFileName(inputSpecFile), ".swagger.json");
        var outputFile = Path.Combine(outputFolder, outputFileName);
        if (verbose)
        {
            await parseResult.InvocationConfiguration.Output.WriteLineAsync($"Writing output file '{outputFile}' as version '{OpenApiSpecVersion.OpenApi2_0}'");
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
        
        async Task CreateSingleExampleFromMultipleExamples(IOpenApiMediaType content, string description)
        {
            if (content is OpenApiMediaType { Example: null, Examples: { Count: not 0} examples } mediaType)
            {
                if (verbose)
                {
                    await parseResult.InvocationConfiguration.Output.WriteLineAsync($"[OpenAPIv2 compatibility] Setting example from first of multiple OpenAPIv3 examples for {description}");
                }

                mediaType.Example = examples.Values.First().Value;
            }
        }

        string GenerateOperationId(HttpMethod operationType, string pathName, IList<IOpenApiParameter>? parameters)
        {
            return string.Join(string.Empty, SplitPathString(operationType, pathName, parameters));

            static string? ToPascalCase(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? value : string.Concat(value[0].ToString().ToUpperInvariant(), value.AsSpan(1));
            }

            static IEnumerable<string> SplitPathString(HttpMethod operationType, string path, IList<IOpenApiParameter>? parameters)
            {
                yield return operationType.ToString().ToLowerInvariant();

                var start = path.StartsWith("/api/", StringComparison.InvariantCultureIgnoreCase)
                    ? path[5..]
                    : path;

                foreach (var split in start.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (split.StartsWith('{'))
                    {
                        break;
                    }

                    if (ToPascalCase(split) is { } s)
                    {
                        yield return s;
                    }
                }

                if (parameters is null or { Count: 0 })
                {
                    yield break;
                }

                yield return "By";

                foreach (var parameter in parameters.Where(it => it.In == ParameterLocation.Path))
                {
                    if (ToPascalCase(parameter.Name) is { } s)
                    {
                        yield return s;
                    }
                }
            }
        }
    }
});

await rootCommand
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);