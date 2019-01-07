// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your Javascript code.
$(document).ready(function () {
    let chevronDown = function () {
        return $.parseHTML("<span class=\"glyphicon glyphicon-chevron-down\"></span>");
    }

    let glyphiconMinus = function () {
        return $.parseHTML("<span class=\"glyphicon glyphicon-minus\"></span>");
    }

    let stateMachine = function () {
        this.states = ["Waiting", "Downloading", "Converting", "Finished"];
        let state = 0;

        this.nextState = function () {
            if (state < states.length() - 1) {
                state++;
            }
        }

        this.getState = function () {
            return state;
        }

        this.getNamedState = function () {
            return this.states[state];
        }
    }

    $("#buttonDownload")[0].addEventListener("click", function () {
        let input = $("#inputDownload")[0];
        let url = input.value;
        let data = { url: url };

        // Find the selected type as well as format and add them to the transported data.
        let selectedFormatInfo = $("#formatSelection").find("li[class='active']").find("a").attr("class").split("_");
        let selectedFormatType = selectedFormatInfo[0];
        let selectedFormat = selectedFormatInfo[1];

        if (selectedFormatType === "video") {
            data.videoFormat = selectedFormat;
        } else {
            data.audioFormat = selectedFormat;
        }

        //input.value = "";

        // Ajax for sending the information to the server.
        fetch(".", {
            method: 'POST',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(data)
        })
            .then(function (response) {
                return response.json();
            })
            .then(function (value) {
                if (value.success === true) {
                    console.log(value);

                    $("#filesEmpty").hide();
                    addListHeader(value.taskIdentifier, value.url);
                }
                else {
                    console.log(value);
                }
            })
            .catch(function (error) {
                console.log(error);
            });

        let addListHeader = function (guid, url) {
            let fileEntry = $("#templateFileEntry").clone();

            // Head:
            fileEntry.attr("id", "fileEntry_" + guid);
            fileEntry.find("#templateHeading").attr("id", "heading_" + guid);
            fileEntry.find("#templateContainerDownloadInfo").attr("id", "containerDownloadInfo_" + guid);
            fileEntry.find("#templateContainerDownloadInfoText").attr("id", "containerDownloadInfoText_" + guid);
            fileEntry.find("#templateContainerDownloadInfoConversion").attr("id", "containerDownloadInfoConversion_" + guid);
            fileEntry.find("#templateContainerDownloadInfoProgress").attr("id", "containerDownloadInfoProgress_" + guid);
            fileEntry.find("#templateContainerDownloadInfoButtonDownload").attr("id", "containerDownloadInfoButtonDownload_" + guid);
            // Change the download link to no toggle the body.
            fileEntry.find("a[href='#templateBody']").first().text(" " + url).attr({ "data-toggle": "", href: "#body_" + guid }).prepend(glyphiconMinus());
            // Body:
            fileEntry.find("#templateBody").attr("id", "body_" + guid);

            fileEntry.prependTo("#fileEntries");

            // Show basic text in order to give users visual feedback.
            let container = fileEntry.find("#containerDownloadInfo_" + guid);
            container.children("div").hide();
            container.find("#containerDownloadInfoText_" + guid).show().find("div[class='infoTextContainer']").text("Link queued...");
        }
    });

    // SignalR code:
    let connection = new signalR.HubConnectionBuilder()
        .withUrl("/ws")
        .configureLogging(signalR.LogLevel.Information)
        .build();

    let start = async function () {
        try {
            await connection.start();
            console.log("connected");
        } catch (err) {
            console.log(err);
            setTimeout(() => start(), 500);
        }
    }

    start();

    connection.onclose(async () => {
        await start();
    });

    connection.on("ReceivedDownloadInfo", (outputInfo) => {
        console.log({ state: "Info received", outputInfo: outputInfo });

        let guid = outputInfo.downloadTaskIdentifier;
        let container = $("#containerDownloadInfo_" + guid);
        container.children("div").hide();
        container.find("#containerDownloadInfoText_" + guid).show().find("div[class='infoTextContainer']").text("Gathering information...");
    });

    connection.on("DownloadFailed", (outputInfo) => {
        console.log({ state: "Failed", outputInfo: outputInfo });


    });

    connection.on("DownloadStarted", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "Started", task: taskIdentifier, result: taskResultIdentifier });

        let guid = taskIdentifier;
        let container = $("#containerDownloadInfo_" + guid);
        container.children("div").hide();
        container.find("#containerDownloadInfoText_" + guid).show().find("div[class='infoTextContainer']").text("Starting download...");
    });

    connection.on("DownloadProgress", (taskIdentifier, resultIdentifier, percentage) => {
        console.log({ state: "Progress", task: taskIdentifier, result: resultIdentifier, percentage: percentage });

        let guid = taskIdentifier;
        let container = $("#containerDownloadInfo_" + guid);
        container.children("div").hide();
        container.find("#containerDownloadInfoProgress_" + guid).show();

        // Order of percentages might not be correct.
        let progressBar = container.find(".progress-bar");
        let currentPercentage = progressBar.text().replace("%","");
        if (percentage > currentPercentage) {
            progressBar.attr("style", "width:" + percentage + "%").text(percentage + "%");
        }
    });

    connection.on("DownloadConversion", (taskIdentifier, resultIdentifier) => {
        console.log({ state: "Conversion", task: taskIdentifier, result: resultIdentifier });

        let guid = taskIdentifier;
        let container = $("#containerDownloadInfo_" + guid);
        container.children("div").hide();
        container.find("#containerDownloadInfoProgress_" + guid).show();
        container.find(".progress-bar").text("Converting... (This could take a while)");
    });

    connection.on("DownloadResultFinished", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "ResultFinished", task: taskIdentifier, result: taskResultIdentifier });

        let guid = taskIdentifier;
        let container = $("#containerDownloadInfo_" + guid);
        container.children("div").hide();
        container.find("#containerDownloadInfoButtonDownload_" + guid).show()
            .find("a").attr("href", "./download?taskIdentifier=" + guid + "&taskResultIdentifier=" + taskResultIdentifier);
    });

    connection.on("DownloadTaskFinished", (taskIdentifier) => {
        console.log({ state: "TaskFinished", task: taskIdentifier });


    });

    connection.on("DownloadInterrupted", (taskIdentifier) => {
        console.log({ state: "Interrupted", task: taskIdentifier });


    });

    connection.on("DownloaderError", (taskIdentifier) => {
        console.log({ state: "DownloaderError", task: taskIdentifier });

        let guid = taskIdentifier;
        let fileEntry = $("#fileEntry_" + guid);
        fileEntry.addClass("panel-danger");
        fileEntry.find("#containerDownloadInfoText_" + guid).show().find("div[class='infoTextContainer']").text("Download failed. See the logs for further information.");
        fileEntry.find("#containerDownloadInfoText_" + guid).find("img").hide();
    });
});