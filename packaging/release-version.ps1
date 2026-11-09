# Works out whether this build gets published, and as which version. Run by .github/workflows/build.yml.
# A merge to main that changes the app or installer is released, adding one to the last number of the newest tag.
# For a bigger jump, raise the version in Directory.Build.props to 1.5.0 or 2.0.0 and the next release uses it.
# Website-only merges aren't released, because GitHub Pages publishes those by itself.

$latest = git tag --list 'v*' --sort=-v:refname | Select-Object -First 1
$last = [version]$latest.TrimStart('v')
$written = [version]([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$version = if ($written -gt $last) { $written } else { [version]::new($last.Major, $last.Minor, $last.Build + 1) }

$changed = git diff --name-only $latest HEAD -- src installer Directory.Build.props
$release = $env:GITHUB_REF -eq 'refs/heads/main' -and [bool]$changed

# The app, the installers and the Store package all read the version from this file. Only CI's copy is changed.
if ($release) {
    (Get-Content Directory.Build.props -Raw) -replace '<Version>.*</Version>', "<Version>$version</Version>" |
        Set-Content Directory.Build.props -NoNewline
}

Write-Host "Newest release is $latest. Publish v$version from this build: $release"
Add-Content $env:GITHUB_OUTPUT "version=$version", "release=$($release.ToString().ToLower())"
