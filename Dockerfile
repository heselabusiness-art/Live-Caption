# syntax=docker/dockerfile:1

# Live Caption Viewer is a .NET 8 WPF desktop app and must be built with
# Windows containers (Docker Desktop -> Switch to Windows containers).
# escape=` (backtick) is required for Windows Dockerfiles.

FROM mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022 AS build
WORKDIR /src

COPY LiveCaptionViewer.csproj ./
COPY App.xaml App.xaml.cs ./
COPY MainWindow.xaml MainWindow.xaml.cs ./
COPY Assets/ ./Assets/
COPY Models/ ./Models/
COPY Services/ ./Services/
COPY Prompt.txt Reminder.txt ./
COPY Individual Prompt V2/ ./Individual Prompt V2/

RUN dotnet restore LiveCaptionViewer.csproj
RUN dotnet publish LiveCaptionViewer.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PlatformTarget=x64 `
    /p:PublishSingleFile=false `
    -o /app/publish

FROM mcr.microsoft.com/dotnet/windowsdesktop:8.0-windowsservercore-ltsc2022 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# WPF/WebView2 need an interactive Windows desktop session to display UI.
# This image packages the app; run the published exe on the host for normal use.
ENTRYPOINT ["LiveCaptionViewer.exe"]
