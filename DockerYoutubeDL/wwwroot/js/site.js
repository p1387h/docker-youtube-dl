// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your Javascript code.
window.addEventListener("load", function () {
    $("#buttonDownload")[0].addEventListener("click", function () {
        let input = $("#inputDownload")[0];
        let url = input.value;
        let data = JSON.stringify({ url: url });

        //input.value = "";

        fetch(".", {
            method: 'POST',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/json'
            },
            body: data
        })
            .then(function (response) {
                return response.json();
            })
            .then(function (value) {
                if (value.success === true) {
                    console.log(value);
                }
                else {
                    console.log(value);
                }
            })
            .catch(function (error) {
                console.log(error);
            });
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
    });

    connection.on("DownloadFailed", (taskIdentifier) => {
        console.log({ state: "Failed", task: taskIdentifier });
    });

    start();
});