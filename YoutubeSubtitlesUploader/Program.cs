﻿using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Collections;
using System.Configuration;
using Google.Apis.Upload;

class Program
{
    static List<String> vttFiles;

    static String selectedFile;

    static string translationFolder;

    static async Task Main(string[] args)
    {
        string videoId;
        string fileName;
        string languageCode;
        string languageName;

        await ListFiles();

        Console.WriteLine();
        await SelectFileToUpload();

        videoId = Path.GetFileNameWithoutExtension(selectedFile);

        fileName = videoId;
        Console.WriteLine($"YouTube video ID : {fileName}");
        
        Dictionary<string, string> languageCodeMap = GetLanguageCodeMapping();

        translationFolder = ConfigurationManager.AppSettings["translationFolder"];
        Console.WriteLine($"Directory with translated files: {translationFolder}");

        YouTubeService youtubeService = await CreateYouTubeService();

        var searchRequest = youtubeService.Videos.List("snippet");
        searchRequest.Id = videoId;
        var searchResponse = await searchRequest.ExecuteAsync();

        Console.WriteLine($"Total videos found: {searchResponse.Items.Count}");

        var youTubeVideo = searchResponse.Items.FirstOrDefault();

        if (youTubeVideo != null)
        {
            var captions = youtubeService.Captions.List("id,snippet", videoId).Execute();
            
            var currentSubtitles =
                captions.Items.Select(c => c.Snippet.Language.ToLower())
                                         .ToList();

            currentSubtitles.ForEach(subtitle => Console.WriteLine($"Subtitle language: {subtitle}"));

            Console.WriteLine($"Current subtitles count: {currentSubtitles.Count}");

            // Get the list of language codes
            var translatedSubtitles = languageCodeMap.Keys.ToList();

            var missingSubtitles = translatedSubtitles.Except(currentSubtitles).ToList();

            Console.WriteLine();
            Console.WriteLine($"Missing subtitles count: {missingSubtitles.Count}");

            // Google Data API is able to upload max of 26 Subtitle languages at a time
            // limit the max number to 25 subtitles in one go

            const int MAX_SUBTITLES_BATCH_SIZE = 25;

            // Add missing subtitles
            foreach (var subtitle in missingSubtitles.Take(MAX_SUBTITLES_BATCH_SIZE))
            {
                languageCode = subtitle;
                languageName = languageCodeMap[subtitle];

                string fileNameWithExtension = string.Concat($@"{fileName}-{languageName}", ".vtt");
            
                string completeFileName = Path.Combine(translationFolder, fileNameWithExtension);

                // Check if the file exists
                if (!File.Exists(completeFileName))
                {
                    Console.WriteLine($"File {fileNameWithExtension} does not exist. Skipping...");
                    continue;
                }

                Console.WriteLine($"Uploading {completeFileName} for language {languageName}");

                await AddVideoCaption(videoId, languageCode, languageName, completeFileName);
            }
        }
        
        await CleanupOlderFiles();

        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
    }

     private static async Task ListFiles()
        {
            string downloadsFolder = ConfigurationManager.AppSettings["downloadsFolder"];
            Console.WriteLine($"Downloads folder : {downloadsFolder}");

            // Check if the folder exists
            if (Directory.Exists(downloadsFolder))
            {

                string[] files = Directory.GetFiles(downloadsFolder, "*.vtt");

                Console.WriteLine($"Number of VTT files in the directory : {files.Length}");

                // Sort the files by creation date in descending order
                vttFiles = files.OrderByDescending(file => File.GetCreationTime(file)).Take(5).ToList();

                
                // Display the files with their name and creation date
                Console.WriteLine();
                Console.WriteLine("The top 5 files sorted by creation date are:");
                int index = 1;
                foreach (var file in vttFiles)
                {
                    Console.WriteLine($"{index}. {Path.GetFileName(file)} ({File.GetCreationTime(file).ToString("F")})");
                    index++;
                }
            }
            else
            {
                Console.WriteLine($"Folder {downloadsFolder} does not exist. Please check the configuration");
            }
        }

        private static async Task SelectFileToUpload()
        {
            Console.WriteLine();
            Console.WriteLine("Please select the file to upload (1-5):");
            int choice = int.Parse(Console.ReadLine());

            // Validate the user input
            if (choice < 1 || choice > 5)
            {
                Console.WriteLine("Invalid input. Please try again.");
                await SelectFileToUpload();
                return;
            }

            // Get the selected video from the list
            selectedFile = vttFiles[choice - 1];

            // Display the selected video information
            Console.WriteLine($"You selected: {selectedFile}");
        }

    private static Dictionary<string, string> GetLanguageCodeMapping()
    {
        var section = (Hashtable)ConfigurationManager.GetSection("CodeLanguageMapping");

        Dictionary<string, string> codeLanguageMap = section.Cast<DictionaryEntry>().ToDictionary(d => (string)d.Key, d => (string)d.Value);

        return codeLanguageMap;

    }
    static async Task AddVideoCaption(string videoID, string languageCode, string languageName, string subtitleFileName) //pass your video id here..
    {
        YouTubeService youtubeService = await CreateYouTubeService();

        // updated mismatched language codes between Microsoft Translator and Youtube API
        if (languageCode == "zh-Hans")
            languageCode = "zh-CN";
        if (languageCode == "pt-pt")
            languageCode = "pt-PT";

        //create a CaptionSnippet object...
        CaptionSnippet capSnippet = new()
        {
            Language = languageCode,
            Name = languageName,
            VideoId = videoID,
            IsDraft = false
        };

        //create new caption object and set the completed snippet
        Caption caption = new()
        {
            Snippet = capSnippet
        };



        //here we read our .srt which contains our subtitles/captions...
        using (var fileStream = new FileStream($@"{subtitleFileName}", FileMode.Open))
        {
            //create the request now and insert our params...
            var captionRequest = youtubeService.Captions.Insert(caption, "snippet", fileStream, "application/atom+xml");

            captionRequest.ProgressChanged += CaptionRequest_ProgressChanged;
            captionRequest.ResponseReceived += CaptionRequest_ResponseReceived;

            //finally upload the request... and wait.
            await captionRequest.UploadAsync();

            Console.WriteLine();
            Console.WriteLine($"Uploaded {subtitleFileName}, in {languageName}");

            Console.WriteLine();
            Console.WriteLine();
        }


    }

    private static async Task<YouTubeService> CreateYouTubeService()
    {
        UserCredential credential;

        //you should go out and get a json file that keeps your information... You can get that from the developers console...
        using (var stream = new FileStream("client_secrets.json", FileMode.Open, FileAccess.Read))
        {
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { YouTubeService.Scope.YoutubeForceSsl, YouTubeService.Scope.Youtube, YouTubeService.Scope.Youtubepartner },
                "user",
                CancellationToken.None
            );
        }
        //creates the service...
        var youtubeService = new YouTubeService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "YoutubeSubtitleUploader"
        });

        return youtubeService;
    }

    private static void CaptionRequest_ResponseReceived(Caption caption)
    {
        Console.WriteLine();
        Console.WriteLine($"{caption.Snippet.Language} caption status = {caption.Snippet.Status}");
    }

    private static void CaptionRequest_ProgressChanged(IUploadProgress progress)
    {
        switch (progress.Status)
        {
            case UploadStatus.Failed:
                Console.WriteLine();
                Console.WriteLine($"An error occured while uploading the subtitles. {Environment.NewLine}{progress.Exception}");
                break;
        }
    }

    private static async Task CleanupOlderFiles()
    {
        Console.WriteLine($"Cleaning up older files in {translationFolder}");

        // Check if the folder exists
        if (Directory.Exists(translationFolder))
        {
            string[] files = Directory.GetFiles(translationFolder, "*.vtt");

            // Sort the files by creation date in descending order
            var filesToDelete = files.Where(file => File.GetCreationTime(file) < DateTime.Now.AddMonths(-2));

            // Delete the older files
            foreach (var file in filesToDelete)
            {
                File.Delete(file);
                Console.WriteLine($"Deleted {file}");
            }

            if(filesToDelete.Count() > 0)
            {
                // Display the confirmation message
                Console.WriteLine("The .VTT files older than 2 months have been deleted.");
            }
        }
    }
}



       