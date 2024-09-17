using Azure.Search.Documents.Indexes;
using Azure;
using Azure.Search.Documents.Indexes.Models;
using Newtonsoft.Json.Linq;
using Azure.Search.Documents.Models;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using static System.Collections.Specialized.BitVector32;

namespace RssVideoProcessor.Services
{
    public class AzureAiSearchService
    {
        private readonly SearchIndexClient _searchIndexClient;
        private readonly SearchIndexerClient _searchIndexerClient;
        private readonly OpenAIClient _openAIClient;
        private const int MODEL_DIMENSIONS = 1536;
        private readonly string _azureOpenApiKey;
        private readonly string _azureOpenAiEndpoint;
        private readonly string _modelName;
        private readonly string _searchIndexName;
        private const string AZURE_OPENAI_EMBEDDING_DEPLOYED_MODEL = "text-embedding-ada-002";

        public AzureAiSearchService()
        {
            _azureOpenApiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
            _azureOpenAiEndpoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
            _modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);

            var apiVersion = Environment.GetEnvironmentVariable("ApiVersion", EnvironmentVariableTarget.Process);
            var searchServiceEndPoint = Environment.GetEnvironmentVariable("SearchServiceEndPoint", EnvironmentVariableTarget.Process);
            var searchIndexName = Environment.GetEnvironmentVariable("SearchIndexName", EnvironmentVariableTarget.Process);
            var searchServiceAdminApiKey = Environment.GetEnvironmentVariable("SearchServiceAdminApiKey", EnvironmentVariableTarget.Process);
            var azureOpenAiEmbeddingDeployedModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYED_MODEL", EnvironmentVariableTarget.Process);
            _searchIndexName = Environment.GetEnvironmentVariable("SearchIndexName", EnvironmentVariableTarget.Process);

            _openAIClient = InitializeOpenAIClient(_azureOpenApiKey, _azureOpenAiEndpoint);
            _searchIndexClient = InitializeSearchIndexClient(searchServiceAdminApiKey, searchServiceEndPoint);
            _searchIndexerClient = InitializeSearchIndexerClient(searchServiceAdminApiKey, searchServiceEndPoint);
        }

        public async Task<SearchIndex> CreateVectorIndex()
        {
            var index = GetSampleIndex();
            var response = await _searchIndexClient.CreateOrUpdateIndexAsync(index);
            return response.Value;
        }

        public async Task<string> IndexPromptContent(string videoName, string videoId, JObject promptContent)
        {
            // if the index doesn't exist, create it
            try
            {
                Response<SearchIndex> response = _searchIndexClient.GetIndex(_searchIndexName);
            }
            catch (RequestFailedException ex)
            {
                if (ex.Status == 404)
                {
                    Console.WriteLine("Creating index...");

                    var index = GetSampleIndex();
                    var response = await _searchIndexClient.CreateOrUpdateIndexAsync(index);
                      
                    await _searchIndexClient.CreateOrUpdateIndexAsync(index);
                }
            }

            var sampleDocuments = await GetSampleDocumentsAsync(videoId, videoName,promptContent);
            var searchClient = _searchIndexClient.GetSearchClient(_searchIndexName);
            var docResponse = await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(sampleDocuments));

            return docResponse.GetRawResponse().Status.ToString();
        }

        private OpenAIClient InitializeOpenAIClient(string adminKey, string endPoint)
        {
            var uri = new Uri(endPoint);

            // Extract the base URL (scheme + authority)
            string baseUrl = uri.GetLeftPart(UriPartial.Authority);

            var credential = new AzureKeyCredential(adminKey);

            return new OpenAIClient(new Uri(baseUrl), credential);
        }

        private SearchIndexClient InitializeSearchIndexClient(string adminKey, string endPoint)
        {
            return new SearchIndexClient(new Uri(endPoint), new AzureKeyCredential(adminKey));
        }

        private SearchIndexerClient InitializeSearchIndexerClient(string adminKey, string endPoint)
        {
            return new SearchIndexerClient(new Uri(endPoint), new AzureKeyCredential(adminKey));
        }

        public async Task<string> GetPromptContentAsync(string videoName)
        {
            var searchClient = _searchIndexClient.GetSearchClient(_searchIndexName);

            var searchOptions = new SearchOptions
            {
                Size = 100
            };

            var results = await searchClient.SearchAsync<SearchDocument>($"videoName:{videoName}", searchOptions);
            
            // Initialize a JArray to hold all the sections
            JArray sectionsArray = new JArray();

            await foreach (SearchResult<SearchDocument> result in results.Value.GetResultsAsync())
            {
                var document = result.Document;
                var start = document["start"].ToString();
                var end = document["end"].ToString();
                var content = document["content"].ToString();

                // Create a JSON object for each section
                var sectionObject = new JObject
                {
                    ["start"] = start,
                    ["end"] = end,
                    ["content"] = content
                };

                // Add the section to the sections array
                sectionsArray.Add(sectionObject);
            }

            // Create the final JSON object to return
            JObject videoJson = new JObject
            {
                ["sections"] = sectionsArray
            };

            return videoJson.ToString();
        }

        private SearchIndex GetSampleIndex()
        {
            string vectorSearchProfile = "prompt-content-vector-profile";
            string vectorSearchHnswConfig = "prompt-content-vector-config";
            string semanticSearchConfig = "prompt-content-semantic-config";

            SearchIndex searchIndex = new(_searchIndexName)
            {
                VectorSearch = new()
                {
                    Profiles =
                {
                    new VectorSearchProfile(vectorSearchProfile, vectorSearchHnswConfig)
                },
                    Algorithms =
                {
                    new HnswAlgorithmConfiguration(vectorSearchHnswConfig)
                }
                },
                SemanticSearch = new()
                {
                    Configurations =
                    {
                        new SemanticConfiguration(semanticSearchConfig, new()
                        {
                            ContentFields =
                            {
                                new SemanticField(fieldName: "content")
                            },
                            TitleField  = new SemanticField(fieldName: "videoName"),
                            KeywordsFields =
                            {
                                new SemanticField("videoName")
                            }
                        })
                    },
                },
                Fields =
                {
                    new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true, IsFacetable = true },
                    new SearchField("videoId", SearchFieldDataType.String),
                    new SearchableField("videoName") { IsSortable = true },
                    new SearchableField("start") { IsFilterable = true, IsSortable = true },
                    new SearchableField("end") { IsFilterable = true, IsSortable = true },
                    new SearchableField("content"),
                    new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                    {
                        IsSearchable = true,
                        VectorSearchDimensions = MODEL_DIMENSIONS,
                        VectorSearchProfileName = vectorSearchProfile
                    },
                }
            };

            return searchIndex;
        }

        private async Task<List<SearchDocument>> GetSampleDocumentsAsync(string videoId, string videoName, JObject promptContent)
        {
            var searchDocuments = new List<SearchDocument>();

            var elementsArray = (JArray)promptContent["sections"];

            foreach (JObject element in elementsArray)
            {
                var searchDocument = new SearchDocument();
                searchDocument["id"] = Guid.NewGuid().ToString();
                searchDocument["videoId"] = videoId;
                searchDocument["videoName"] = videoName;

                searchDocument["start"] = element["start"].ToString();
                searchDocument["end"] = element["end"].ToString();
                searchDocument["content"] = element["content"].ToString();

                float[] contentEmbeddings = (await GenerateEmbeddings(element["content"].ToString())).ToArray();

                searchDocument["contentVector"] = contentEmbeddings;
                searchDocuments.Add(new SearchDocument(searchDocument));
            }

            return searchDocuments;
        }

        // Function to generate embeddings  
        private async Task<IReadOnlyList<float>> GenerateEmbeddings(string text)
        {
            var response = await _openAIClient.GetEmbeddingsAsync(AZURE_OPENAI_EMBEDDING_DEPLOYED_MODEL, new EmbeddingsOptions(text));
            return response.Value.Data[0].Embedding;
        }
    }
}
