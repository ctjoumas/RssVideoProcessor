

using Azure.AI.OpenAI;
using Azure;
using OpenAI.Chat;

public class AzureOpenAIService
{
    private readonly AzureOpenAIClient _azureOpenAIClient;
    private readonly ChatClient _chatClient;

    private const string SystemPrompt = @"
        You are an AI assistant that analyzes insights from a video and extracts key decisions that were made in the meetings from the speakers. 
        You will be given structured JSON in the following format:
        ""sections"": [
            {
              ""id"": 0,
              ""start"": ""0:00:00"",
              ""end"": ""0:00:28.12"",
              ""content"": ""[Video title] testName\n[Tags] Beginning\n[Visual labels] logo, font, colorfulness, tree, building, outdoor, sky, cloud, indoor, furniture, court\n[OCR] 08.26.24, DENVER, THE MILE HIGH CITY, CITY COUNCIL, LEGISLATIVE SESSION, NOW, DENVER CITY COUNCIL, WEEKLY LEGISLATIVE SESSION WITH ALL COUNCIL MEMBERS\n[Transcript] Welcome to your Denver City Council.\nPlease stand by.
               \nFull coverage of your Denver City Council begins now.\nGood afternoon, everyone.""
            },
            {
              ""id"": 1,
              ""start"": ""0:00:28.12"",
              ""end"": ""0:03:04.92"",
              ""content"": ""[Video title] testName\n[Tags] Beginning\n[Detected objects] chair, cup, laptop\n[Visual labels] indoor, furniture, human face, laptop, computer, person, 
                clothing, chair, man, flag, smile, woman\n[OCR] 08.26.24, DENVER CITY COUNCIL, WEEKLY LEGISLATIVE SESSION WITH ALL COUNCIL MEMBERS, DELL, DeLL, GILMORE, DEL\n[Transcript] Thank you for joining us.\nTonight's meeting is being interpreted into Spanish.\nSam, would you please introduce yourself and let our viewers know how to enable translation on their devices?\nYes, of course.\nThank you for having us today.\nGood afternoon.\nMy name is Sam Guzman with the CLC, and along with my colleague Alejandro, we will be interpreting today's meeting into Spanish.\nI'm going to give the instructions in Spanish on how to access interpretation.\nBuenastaris Atos MI nom de Samuel Guzman con la SE El SE Y contamente comico le Alejandro esta remos interpretando la reignon de oy El espanol sis Nos a companion oya travez zoom Vito almente por favor busquin supantaya unicono de Globo querice interpretacion O prima seboton EDI selecione a la opcion Perez cuchar en espanol muchas gracias and thank you very much.\nThank you, Sam.\nWelcome to the Denver City Council meeting of Monday, August 26th, 2024.\nCouncil members, please rise as you are able and join Council Member Gilmore in the Pledge of Allegiance.\nCouncil members, please join Council Member Gilmore as they lead us in the Denver City Council Land Acknowledgement.\nThank you, Council President.\nThe Denver City Council honors and acknowledges that the land on which we reside is the traditional territory of the Ute Cheyenne and Arapahoe peoples.\nWe also recognize the 48 contemporary tribal nations that are historically tied to the lands that make up the State of Colorado.\nWe honor Elders past, present, and future, and those who have stewarded this land throughout generations.\nWe also recognize that government, academic, and cultural institutions were founded upon and continue to enact exclusions and erasures of Indigenous peoples.\nMay this acknowledgement demonstrate a commitment to working to dismantle ongoing legacies of oppression and inequalities and recognize the current and future contributions of Indigenous communities in Denver.\nThank you, Councilwoman.\nMadam Secretary, Roll.""
            }
        ]

        Each node of the JSON is a section extracted from the video analysis. If a key decision is made during the meeting, you will return the following list of JSON items for each key decision:

        {
        ""extracted_decision"": [
        {
        ""start"": ""01:24:22"",
        ""end"":""01:26:11"",
        ""key_decision"": ""John mentioned that the decision was made to extend the school day by 15 minutes each day in order to make up for the number 
            of snow days that took place during the school year""
        },
        {
        ""start"": ""01:41:22"",
        ""end"":""01:42:11"",
        ""key_decision"": ""Judy said that the decision was made to add an extra day of PE to each week of school""
        }]
        }";


    public AzureOpenAIService()
    {
        var apiKey = Environment.GetEnvironmentVariable("AzureOpenAIKey", EnvironmentVariableTarget.Process);
        var endPoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint", EnvironmentVariableTarget.Process);
        var modelName = Environment.GetEnvironmentVariable("ModelName", EnvironmentVariableTarget.Process);

        _azureOpenAIClient = new(
            new Uri(endPoint), new AzureKeyCredential(apiKey));

        _chatClient = _azureOpenAIClient.GetChatClient(modelName);
    }

    public async Task<string> GetChatResponseAsync(string prompt)
    {
        ChatCompletion completion = await _chatClient.CompleteChatAsync(
        [
            // System messages represent instructions or other guidance about how the assistant should behave
            new SystemChatMessage($"{SystemPrompt}\n\n Only use the provided context, do not reply otherwise. Context: {prompt}"),
            // User messages represent user input, whether historical or the most recen tinput
            new UserChatMessage($"Using the provided context, please scan the content to determine if any key decisions were made.")
        ]);


        return completion.Content[0].Text;
    }
}