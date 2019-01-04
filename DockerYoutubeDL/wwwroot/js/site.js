// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your Javascript code.
window.addEventListener("load", function () {
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
                    hideNoFiles();
                    addListHeader(value.taskIdentifier, value.url);
                }
                else {
                    console.log(value);
                }
            })
            .catch(function (error) {
                console.log(error);
            });

        let hideNoFiles = function () {
            $("#filesEmpty").attr("hidden", true);
        }

        let addListHeader = function (guid) {

        }
    });

    // SignalR code:
    let connection = new signalR.HubConnectionBuilder()
        .withUrl("/ws")
        .configureLogging(signalR.LogLevel.Information)
        .build();

    let start = async function () {
        //try {
            await connection.start();
            console.log("connected");
        //} catch (err) {
        //    console.log(err);
        //    setTimeout(() => start(), 5000);
        //}
    }

    connection.onclose(async () => {
        await start();
    })

    connection.on("DownloadFinished", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "Finished", task: taskIdentifier, result: taskResultIdentifier });
    });

    connection.on("DownloadFailed", (taskIdentifier) => {
        console.log({ state: "Failed", task: taskIdentifier });
    });

    start();
});