using JetBrains.Annotations;
using Newtonsoft.Json;
using Octokit;
using Octokit.Helpers;

namespace ProjectMirrorCollapse;

public static class MirrorCollapse {
  [UsedImplicitly(ImplicitUseTargetFlags.Members)]
  public static class MirrorCollapseSettings {
    public static bool    GithubOAuth;
    public static string? GithubOAuthToken;
    public static string? GithubAuthUser, GithubAuthPass;

    public static string? OriginRepo, UpstreamRepo;

    public static string? PRTitlePrefix, PRBodyPrefix;

    public const string SettingsFile = "MirrorCollapseSettings.json";

    public static void Load() {
      var loadFrom = new FileInfo(Path.Join(_mirrorCollapseDataDirectory.FullName, SettingsFile));
      if(!loadFrom.Exists)
        return;

      using var stream = new StreamReader(loadFrom.OpenRead());
      var       dict   = JsonConvert.DeserializeObject<Dictionary<string, object?>>(stream.ReadToEnd()) ?? throw new JsonSerializationException();
      foreach(var (key, value) in dict) {
        var field = typeof(MirrorCollapseSettings).GetField(key);
        if(field is null || field.IsLiteral || field.IsInitOnly) {
          Console.Error.WriteLine($"Illegal Setting: '{key}' doesn't exist or is not valid.");
          continue;
        }

        try {
          field.SetValue(null, value);
        } catch(ArgumentException) {
          Console.Error.WriteLine($"Illegal Setting: '{key}' invalid Type");
        }
      }
    }

    public static void Save() {
      var loadFrom = new FileInfo(Path.Join(_mirrorCollapseDataDirectory.FullName, SettingsFile));
      if(loadFrom.Exists)
        loadFrom.Delete();
      using var write = loadFrom.CreateText();
      var       dict  = new Dictionary<string, object?>();
      foreach(var field in typeof(MirrorCollapseSettings).GetFields()) {
        if(field.IsInitOnly || field.IsLiteral)
          continue;
        dict[field.Name] = field.GetValue(null);
      }

      write.Write(JsonConvert.SerializeObject(dict, Formatting.Indented));
      write.Flush();
      write.Close();
    }
  }


  private static DirectoryInfo     _mirrorCollapseDataDirectory = null!;
  private static GitHubClient?     _githubClient;
  private static ReferencesClient? _refClient;

  private static Repository? _originRepository, _upstreamRepository;

  public const string ProductName    = "MirrorCollapse",
                      ProductVersion = "0.0.1";

  public static async Task Main(string[] args) {
    _mirrorCollapseDataDirectory = new DirectoryInfo(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MirrorCollapse"));
    Console.WriteLine($"Data Directory: {_mirrorCollapseDataDirectory.FullName}");
    if(!_mirrorCollapseDataDirectory.Exists)
      _mirrorCollapseDataDirectory.Create();
    MirrorCollapseSettings.Load();
    MirrorCollapseSettings.Save();


    var connection = new Connection(new ProductHeaderValue(ProductName, ProductVersion)) {
                                                                                           Credentials = MirrorCollapseSettings.GithubOAuth
                                                                                                           ? new Credentials(MirrorCollapseSettings.GithubOAuthToken)
                                                                                                           : new Credentials(MirrorCollapseSettings.GithubAuthUser, MirrorCollapseSettings.GithubAuthPass)
                                                                                         };
    _githubClient = new GitHubClient(connection);
    _refClient    = new ReferencesClient(new ApiConnection(connection));

    var resp = await CalculateMissingPulls();
    Console.WriteLine($"Found {resp.Count} missing PRs.");
    var verified = await VerifyMissingPulls(resp);
    Console.WriteLine($" Of which {verified.Count} are actually missing.");
    foreach(var pull in verified) {
      await MirrorPR(pull);
    }
  }

  public static async Task PopulateRepos() {
    if(_githubClient is null) {
      Console.WriteLine("Critical: Github Client failed to populate.");
      return;
    }

    if(MirrorCollapseSettings.OriginRepo is null || MirrorCollapseSettings.UpstreamRepo is null) {
      Console.WriteLine("Critical: Either OriginRepo or UpstreamRepo are not set.");
      return;
    }

    var origin   = MirrorCollapseSettings.OriginRepo.Split("/");
    var upstream = MirrorCollapseSettings.UpstreamRepo.Split("/");

    if(origin.Length != 2 || upstream.Length != 2) {
      Console.WriteLine("Critical: Either OriginRepo or UpstreamRepo are invalid.");
      return;
    }

    _originRepository   = await _githubClient.Repository.Get(origin[0],   origin[1]);
    _upstreamRepository = await _githubClient.Repository.Get(upstream[0], upstream[1]);

    if(_originRepository is null || _upstreamRepository is null) {
      Console.WriteLine("Warning: Failed to populate repositories.");
    }

    await VerifyMirrorBranch();
  }

  public static async Task VerifyMirrorBranch() {
    try {
      await _githubClient!.Repository.Branch.Get(_originRepository!.Id, ProductName);
    } catch(NotFoundException) {
      await _refClient.CreateBranch(_originRepository!.Owner.Login, _originRepository.Name, ProductName);
    }

    try {
      await _githubClient!.Repository.Content.GetAllContentsByRef(_originRepository.Id, "mirrored.json", ProductName);
    } catch(NotFoundException) {
      await CreateFile(_originRepository.Id, "mirrored.json", ProductName, JsonConvert.SerializeObject(new List<ulong>()));
    }
  }

  public static async Task CreateFile(long repoID, string filepath, string branch, string content) {
    await _githubClient!.Repository.Content.CreateFile(repoID, filepath, new CreateFileRequest($"create {filepath}", content, branch));
  }

  public static async Task WriteToFile(long repoID, string filepath, string branch, string content) {
    var fileRef = await _githubClient!.Repository.Content.GetAllContentsByRef(repoID, filepath, branch);
    if(fileRef.Count != 1) throw new IOException();
    var file          = fileRef[0];
    var updateRequest = new UpdateFileRequest($"update {filepath}", content, file.Sha, branch);
    await _githubClient.Repository.Content.UpdateFile(repoID, filepath, updateRequest);
  }

  public static async Task<string> ReadFromFile(long repoID, string filepath, string branch) {
    var fileRef = await _githubClient!.Repository.Content.GetAllContentsByRef(repoID, filepath, branch);
    if(fileRef.Count != 1) throw new IOException();
    var file = fileRef[0];
    return file.Content;
  }

  public static async Task<List<long>> GetMirroredPulls() {
    if(_originRepository is null) {
      Console.WriteLine("Critical: Origin repository is not populated.");
      return new List<long>();
    }

    var mirrored = await ReadFromFile(_originRepository.Id, "mirrored.json", ProductName);
    return JsonConvert.DeserializeObject<List<long>>(mirrored) ?? throw new JsonSerializationException();
  }

  public static async Task AddMirroredPull(long pull) {
    var mirrored = await GetMirroredPulls();
    mirrored.Add(pull);
    var newContent = JsonConvert.SerializeObject(mirrored);
    await WriteToFile(_originRepository!.Id, "mirrored.json", ProductName, newContent);
  }

  public static async Task<List<PullRequest>> CalculateMissingPulls() {
    await PopulateRepos();

    if(_upstreamRepository is null || _originRepository is null) {
      Console.WriteLine("Critical: Failed to populate repositories.");
      return new List<PullRequest>();
    }

    // TODO Octokit seems to fail when we attempt to do this the correct way
    // var request = new PullRequestRequest {
    //                                        State         = ItemStateFilter.Closed,
    //                                        SortProperty = PullRequestSort.Updated
    //                                      };
    // var upstreamPulls = await _githubClient!.PullRequest.GetAllForRepository(_upstreamRepository.Id, request);
    // Console.WriteLine($"Fetched {upstreamPulls.Count} Pulls.");
    // var mergedPulls = (from pull in upstreamPulls where pull.Merged select pull).ToList();
    // Console.WriteLine($" Of which {mergedPulls.Count} are merged.");
    // var cachedMirrors = await GetMirroredPulls();
    // var newPulls      = (from pull in mergedPulls where !cachedMirrors.Contains(pull.Id) select pull).ToList();
    // Console.WriteLine($" Of which {newPulls.Count} are new and not cached.");

    // WOW THIS IS UGLY INNIT
    var resp       = await _githubClient!.Connection.GetHtml(new Uri($"https://api.github.com/repos/{_upstreamRepository.Owner.Login}/{_upstreamRepository.Name}/pulls?state=closed"), new Dictionary<string, string>());
    var respString = (string)resp.HttpResponse.Body;

    var start      = respString.IndexOf("\"number\"", StringComparison.Ordinal) + 9;
    var end        = respString.IndexOf(",", start, StringComparison.Ordinal);
    var newestPull = int.Parse(respString[start .. end]);

    var toCheck = new List<PullRequest>();
    for(var val = newestPull; val > newestPull - 100; val--) {
      try {
        var pull = await _githubClient.Repository.PullRequest.Get(_upstreamRepository.Id, val);
        toCheck.Add(pull);
      } catch(NotFoundException) { }
    }

    Console.WriteLine($"Fetched {toCheck.Count} pulls.");
    var mergedPulls = (from pull in toCheck where pull.Merged select pull).ToList();
    Console.WriteLine($" Of which {mergedPulls.Count} are merged.");
    var cachedMirrors = await GetMirroredPulls();
    var newPulls      = (from pull in mergedPulls where !cachedMirrors.Contains(pull.Id) select pull).ToList();
    Console.WriteLine($" Of which {newPulls.Count} are new and not cached.");

    return newPulls;
  }

  public static async Task<List<PullRequest>> VerifyMissingPulls(List<PullRequest> pulls) {
    var ret = new List<PullRequest>(pulls);
    Console.Write("Verifying Pulls: ");
    var i = 0;
    foreach(var pull in pulls) {
      if(i++ > 9) {
        i = 0;
        Console.WriteLine("Verifying Pulls: ");
      }

      Console.Write($"{pull.Number}, ");
      try {
        var commit = await _githubClient!.Repository.Commit.Get(_originRepository!.Id, pull.MergeCommitSha);
        if(commit.Repository?.Id != _originRepository.Id) // No repository, or not the origin = missing
          continue;
        await AddMirroredPull(pull.Number);
        ret.Remove(pull);
      } catch(NotFoundException) { }
    }

    return ret;
  }

  public static async Task MirrorPR(PullRequest pull) {
    Console.WriteLine($"Mirroring Pull {pull.Number}");
    var       branchName = $"mirror-{pull.Number}";
    var       branchRef  = $"heads/mirror-{pull.Number}";

    try {
      await _refClient!.Get(_originRepository!.Id, branchRef);
    } catch(NotFoundException) {
      var newReference = new NewReference($"refs/{branchRef}", pull.MergeCommitSha);
      await _refClient!.Create(_originRepository!.Id, newReference);
    }

    await _refClient.Update(_originRepository.Id, branchRef, new ReferenceUpdate(pull.MergeCommitSha, true));
    var pr = new NewPullRequest($"[MIRROR]{MirrorCollapseSettings.PRTitlePrefix} {pull.Title}", branchName, _originRepository.DefaultBranch) {
                                                                                                          Body = $"-- Mirror Pull Request - MirrorCollapse -- \n{MirrorCollapseSettings.PRBodyPrefix}\n" + pull.Body
                                                                                                        };
    try {
      await _githubClient!.Repository.PullRequest.Create(_originRepository.Id, pr);
    } catch(ApiValidationException apiE) {
      if(apiE.HttpResponse.Body is not string apiResp)
        return;
      if(apiResp.Contains("No commits between master and"))
        await AddMirroredPull(pull.Number);
      return;
    }
    await AddMirroredPull(pull.Number);
  }
}
