# docker-youtube-dl
docker-youtube-dl is a web application wrapping the youtube-dl command line tool in an easier to access docker container. The included dockerfile contains the installation of the needed components to run the tool as well as the web interface, which is build with .Net Core, Razor Pages and SignalR.
Creator: P H, ph1387@t-online.de 

---

## Overview
<p align="center">
  <img width="1000" height="418" src="https://github.com/ph1387/docker-youtube-dl/blob/master/youtubedl.gif">
</p>

The application is based on the [youtube-dl](https://github.com/rg3/youtube-dl) command line tool in such a way that it encapculates the main functionality of downloading video and audio files from (mostly) youtube and converting them into the desired format. The provided interface is therefore only presenting the most essential features like choosing format and the option (for videos) to download video and audio files separately and merging them together. By default the application is always downloading the **best** video format possible which means the best single file combination of video and audio that the tool can find. The user can toggle between this behaviour and merging the best tracks together. The latter one will considerably increase the time for each download and require a lot of CPU power. Audio files such as mp3 and wav are not affected by this and are always downloaded in the best format and are then converted into the desired one.

With this application it is possible to download either single files or whole playlists as the tool will queue up any link it can find. Videos that are not available are skipped and not included in the resulting collection. Furthermore the format chosen at the top defines the format of each file that this application downloads. I.e. a playlists with "mp3" selected will download all files as ".mp3". A finished, downloaded playlist will allow the user to download it zipped as a whole without needing to click each entry separately.

Downloads can be interrupted and files that are no longer needed can be deleted by pressing one of the "bin" icons. The ones next to the urls delete/interrupt only the task that they are associated with while the top most one deletes all entries in the list. Each download can be seen/downloaded/interrupted by each user and entries are updated via a websocket connection that is automatically created when visiting the site. When losing this connection all entries are hidden and the user is prompted to refresh the page. This can be done by pressing either the "refresh" icon or the navbar at the top. Navigating to the url works as well.

## Instructions

### How to run
Clone the project and navigate into the docker-youtube-dl folder (it's the one containing the DockerYoutubeDL.sln file) and execute the docker build command as shown below. This creates two images, one unnamed and one named dockeryoutubedl. The unnamed one can be deleted since it is a leftover of the build process. The base and resulting temporay images are quite large in comparison to the result so make sure that you have enough space before building the image. The resulting one exposes port 80 for you to map to a port to your liking.

|Image|Size|
|-|-|
|Base .Net Core sdk|~1.73GB|
|Base .Net Core runtime|~253MB|
|unnamed temporary one|~1.73GB|
|dockeryoutubedl|~377MB|

```sh
docker build -t dockeryoutubedl -f ./DockerYoutubeDL/Dockerfile .
```

After building the image with the provided Dockerfilde located in the project folder run the following command to start a container. This creates a detached (-d) container listening on port 80 (-p 80:80) named youtubedl. The skd, runtime and temporary image can be deleted.

```sh
docker run -d -p 80:80 --name youtubedl dockeryoutubedl
```

### How to configure
These environment variables can be changed when starting the container:

|Environment variable|Assigned default value|Description|
|-|-|-|
|BasePath|/|The base path of the application. I.e.: localhost/API can be changed to localhost/customBasePath/API.|
|Logging:LogLevel:Default|Warning|The log level of the .Net Core application [Trace, Debug, Information, Warning, Error, Critical, None].|

Example:

```sh
docker run -d -p 80:80 --name youtubedl-e Logging:LogLevel:Default=Debug dockeryoutubedl
```

### How it works
The dockerfile contains all components that are needed for this application to work. It is based on the default .Net Core sdk/runtime combination for building cross platform applications in .Net. The ASP .Net Core Razor Pages project is compiled in the sdk and later copied into the runtime image. After that curl, ffmpeg/avconv, python as well as youtube-dl are installed.

Each time a user enters a link in the input field, a new DownloadTask is stored in the in memory database of the application and a [Hangfire](https://github.com/HangfireIO/Hangfire) background job is created. This job starts the two services (info/download) and retrieves information about the download target with the former one. After that the files are downloaded with the download service. Both work in conjuction with [youtube-dl](https://github.com/rg3/youtube-dl), a command line tool for downloading video resources from various sites, which output is redirected and matched with various regular expressions.
A DownloadTask (= a single user input) is split into one (single file) or multiple (playlists) DownloadResults which are used for differentiating between each ongoing download, all while the services notify the user about any progess they make via a websocket connection based on SignalR. The downloaded files are then stored in a directory named after the id of the task that they belong to, which allows for multiple tasks to download the same file if needed. Each download can be interrupted and each file can be removed by clicking the "bin" icons next to the target DownloadTask. This kills any process associated with it and removes all traces from the database as well as the file system. [Polly](https://github.com/App-vNext/Polly) ensures this by retrying the delete operations with increasing time between each try.

## References
- [youtube-dl](https://github.com/rg3/youtube-dl)
- [Hangfire](https://github.com/HangfireIO/Hangfire)
- [Polly](https://github.com/App-vNext/Polly)
- [Bootstrap Checkboxes/Radios](https://bootsnipp.com/snippets/ZkMKE)

## License
MIT [license](https://github.com/ph1387/docker-youtube-dl/blob/master/LICENSE.txt)
