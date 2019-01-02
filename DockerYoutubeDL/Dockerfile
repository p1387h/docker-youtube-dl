FROM corebase AS base
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