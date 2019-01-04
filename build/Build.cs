using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.ControlFlow;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.SerializationTasks;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Logger;
using static Nuke.Common.Tools.Git.GitTasks;
using static TeamCityManager;

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.CreateSolution);

    [Parameter] readonly bool Https;

    string Solution => RootDirectory / "nuke.sln";
    string Organization => "nuke-build";
    
    AbsolutePath RepositoriesDirectory => RootDirectory / "repositories";
    AbsolutePath RepositoriesFile => RootDirectory / "repositories.yml";
    IEnumerable<GitRepository> Repositories =>
        YamlDeserializeFromFile<string[]>(RepositoriesFile)
            .Select(x => x.Split(separator: '#'))
            .Select(x => GitRepository.FromUrl(url: x.First(), branch: x.ElementAtOrDefault(index: 1) ?? "master"));

    Target CreateSolution => _ => _
        .Executes(() =>
        {
            foreach (var repository in Repositories)
            {
                var repositoryDirectory = RepositoriesDirectory / repository.Identifier;
                var origin = Https ? repository.HttpsUrl : repository.SshUrl;
                var branch = repository.Branch;

                if (!Directory.Exists(repositoryDirectory))
                    Git($"clone {origin} {repositoryDirectory} --branch {branch} --progress");
                else
                {
                    SuppressErrors(() => Git($"remote add origin {origin}", repositoryDirectory));
                    Git($"remote set-url origin {origin}", repositoryDirectory);
                }
            }
            
            PrepareSolution();
        });

    [Parameter] readonly string ProjectName;
    string ProjectNameDashed => ProjectName.ToLowerInvariant().Replace(".", "-");
    [Parameter] readonly string[] Description;
    [Parameter] readonly string DefaultBranch = "master";
    [PathExecutable] readonly Tool Hub;
    
    Target AddProject => _ => _
        .Requires(() => ProjectName)
        .Requires(() => Description)
        .Executes(() =>
        {
            var repositoryDirectory = RepositoriesDirectory / Organization / ProjectNameDashed;
            using (SwitchWorkingDirectory(repositoryDirectory))
            {
                CopyTemplate(repositoryDirectory);
                EnsureCleanDirectory(repositoryDirectory / ".git");
                ExecuteWithRetry(PrepareSolution, retryAttempts: 5);

                Git("init");
                Git($"checkout -b {DefaultBranch}");
                Git($"commit -m {"Initialize repository".DoubleQuote()} --allow-empty");
                Git("add .");
                Git($"commit -m {"Add template files".DoubleQuote()}");
                // Hub($"create {Organization}/{LispName} -d {Description.JoinSpace().DoubleQuoteIfNeeded()} -h https://nuke.build");
            }

            var updatedRepositories = Repositories
                .Concat(GitRepository.FromUrl($"https://github.com/{Organization}/{ProjectNameDashed}", DefaultBranch))
                .Select(x => $"{x.HttpsUrl}#{x.Branch}").OrderBy(x => x).ToList();
            YamlSerializeToFile(updatedRepositories, RepositoriesFile);
            Git($"add {RepositoriesFile}");
            Git($"commit -m {$"Add {ProjectNameDashed}".DoubleQuote()}");
        });

    void CopyTemplate(AbsolutePath repositoryDirectory)
    {
        var templateDirectory = RepositoriesDirectory / Organization / "template";
        CopyDirectoryRecursively(templateDirectory, repositoryDirectory);

        var replacements = new Dictionary<string, string>
                           {
                               { "Template", ProjectName },
                               { "template", ProjectNameDashed }
                           };
        new[]
        {
            (RelativePath) ".nuke",
            (RelativePath) "nuke-template.sln",
            (RelativePath) "src" / "Nuke.Template.Tests" / "Nuke.Template.Tests.csproj"
        }.ForEach(x => FillTemplateFile(repositoryDirectory / x, replacements: replacements));

        GlobDirectories(repositoryDirectory, "**/Nuke.*").ToList()
            .ForEach(x => Directory.Move(x, x.Replace("Template", ProjectName)));
        GlobFiles(repositoryDirectory, "**/Nuke.*").ToList()
            .ForEach(x => File.Move(x, x.Replace("Template", ProjectName)));
        GlobFiles(repositoryDirectory, "nuke-*").ToList()
            .ForEach(x => File.Move(x, x.Replace("template", ProjectNameDashed)));
    }

    Target Readme => _ => _
        .Executes(() =>
        {
            ReadmeManager.WriteReadme(RootDirectory / "README.md", RepositoriesDirectory);
        });

    string TeamCityConfiguration => RootDirectory / ".teamcity" / "settings.kts";
    
    Target CreateTeamCity => _ => _
        .Executes(() =>
        {
            WriteTeamCityConfiguration(TeamCityConfiguration, Repositories.ToList());
        });

    IDisposable SwitchWorkingDirectory(string workingDirectory, bool allowCreate = true)
    {
        if (!Directory.Exists(workingDirectory))
            EnsureCleanDirectory(workingDirectory);

        var previousWorkingDirectory = EnvironmentInfo.WorkingDirectory;
        return DelegateDisposable.CreateBracket(
            () => Directory.SetCurrentDirectory(workingDirectory),
            () => Directory.SetCurrentDirectory(previousWorkingDirectory));
    }

    void FillTemplateFile(
        string templateFile,
        IReadOnlyCollection<string> definitions = null,
        IReadOnlyDictionary<string, string> replacements = null)
    {
        var templateContent = ReadAllText(templateFile);
        WriteAllText(templateFile, TemplateUtility.FillTemplate(templateContent, definitions, replacements));
    }
}