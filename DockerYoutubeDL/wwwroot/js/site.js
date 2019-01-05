// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your Javascript code.
$(document).ready(function () {
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

        let chevronDown = function () {
            return $.parseHTML("<span class=\"glyphicon glyphicon-chevron-down\"></span>");
        }

        let glyphiconMinus = function () {
            return $.parseHTML("<span class=\"glyphicon glyphicon-minus\"></span>");
        }

        let addListHeader = function (guid, url) {
            let fileEntry = $("#templateFileEntry").clone();

            // Head:
            fileEntry.attr("id", "fileEntry_" + guid).attr("hidden", false);
            fileEntry.find("#templateHeading").attr("id", "heading_" + guid);
            fileEntry.find("a").first().text(" " + url).attr({ "data-toggle": "", href: "#body_" + guid }).prepend(glyphiconMinus());
            fileEntry.find("#templateContainerButtonDownload").attr("id", "containerButtonDownload_" + guid);
            fileEntry.find("#templateMessageToUser").attr("id", "messageToUser_" + guid);
            fileEntry.find("#templateLoading").attr("id", "loading_" + guid);

            // Body:
            fileEntry.find("#templateBody").attr("id", "body_" + guid);

            fileEntry.prependTo("#fileEntries");
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
            setTimeout(() => start(), 5000);
        }
    }

    connection.onclose(async () => {
        await start();
    })

    connection.on("DownloadFinished", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "Finished", task: taskIdentifier, result: taskResultIdentifier });

        let guid = taskIdentifier;
        let fileEntry = $("#fileEntry_" + guid);
        fileEntry.find("#loading_" + guid).hide();
        fileEntry.find("#containerButtonDownload_" + guid).show()
            .find("a").attr("href", "./download?taskIdentifier=" + guid + "&taskResultIdentifier=" + taskResultIdentifier);
    });

    connection.on("DownloadFailed", (taskIdentifier) => {
        console.log({ state: "Failed", task: taskIdentifier });

        let guid = taskIdentifier;
        let fileEntry = $("#fileEntry_" + guid);
        fileEntry.addClass("panel-danger");
        fileEntry.find("#loading_" + guid).hide();
        fileEntry.find("#messageToUser_" + guid).text("Download failed. See the logs for details.").show();
    });

    connection.on("DownloadStarted", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "Started", task: taskIdentifier, result: taskResultIdentifier });

        let guid = taskIdentifier;
        let fileEntry = $("#fileEntry_" + guid);
        fileEntry.find("#messageToUser_" + guid).hide();
        fileEntry.find("#loading_" + guid).show();
    });

    connection.on("ReceivedDownloadInfo", (downloadResult) => {
        console.log({ state: "Info received", result: downloadResult });
    });

    connection.on("Ping", () => {
        console.log("Replying to ping");
        connection.invoke("Pong");
    });

    start();
});