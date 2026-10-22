# escape=`

# Live Caption Viewer is a .NET 10 WPF desktop app and must be built with
# Windows containers (Docker Desktop -> Switch to Windows containers).
# The parser directive above has to stay on the very first line: the backtick is
# the line-continuation character, so Windows paths can keep their backslashes.

FROM mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022 AS build
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY LiveCaptionViewer.csproj ./
RUN dotnet restore LiveCaptionViewer.csproj

# Everything else (sources, prompts, assets). .dockerignore keeps bin/obj, tests and the
# Chrome extension out of the context, so new source folders never need a Dockerfile edit.
COPY . .

RUN dotnet publish LiveCaptionViewer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PlatformTarget=x64 `
    /p:PublishSingleFile=false `
    -o /app/publish

# The publish is self-contained, so the runtime stage only needs a Windows base image.
FROM mcr.microsoft.com/windows/servercore:ltsc2022 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# WPF/WebView2 need an interactive Windows desktop session to display UI.
# This image packages the app; run the published exe on the host for normal use.
ENTRYPOINT ["LiveCaptionViewer.exe"]
