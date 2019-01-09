FROM microsoft/dotnet:2.1-aspnetcore-runtime AS base
WORKDIR /app
EXPOSE 80

FROM microsoft/dotnet:2.1-sdk AS build
WORKDIR /src
COPY ["DockerYoutubeDL/DockerYoutubeDL.csproj", "DockerYoutubeDL/"]
RUN dotnet restore "DockerYoutubeDL/DockerYoutubeDL.csproj"
COPY . .
WORKDIR "/src/DockerYoutubeDL"
RUN dotnet build "DockerYoutubeDL.csproj" -c Release -o /app

FROM build AS publish
RUN dotnet publish "DockerYoutubeDL.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "DockerYoutubeDL.dll"]
# Install avconv, python and curl for youtube-dl.
RUN apt-get update && \
	apt-get -y install libav-tools && \
	apt-get -y install python && \
	apt-get install -y curl
# Install youtube-dl using curl.
RUN curl -L https://yt-dl.org/downloads/latest/youtube-dl -o /usr/local/bin/youtube-dl
RUN chmod a+rx /usr/local/bin/youtube-dl
# Create download folder for youtube-dl.
RUN mkdir /downloads
# Set environment variables.
ENV DownloadRootFolder=/downloads
ENV FfmpegLocation /usr/bin/ffmpeg
ENV YoutubeDlLocation /usr/local/bin/youtube-dl