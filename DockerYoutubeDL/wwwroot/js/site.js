// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your Javascript code.
$(document).ready(function() {
    // Logging settings.
    let allowLogging = false;
    let oldLog = console.log;
    // Values: [Error, Warning, Information, Trace]
    let signalRLogLevel = signalR.LogLevel.Warning;

    console.log = function(message) {
        if (allowLogging) {
            oldLog(message);
        }
    }

    // Format selection must change the different fields of the form.
    $("#formatSelection li a").on("click", function () {
        let selectedFormatInfo = $(this).attr("class").split("_");
        let selectedFormatType = selectedFormatInfo[0];
        let selectedFormat = selectedFormatInfo[1];

        // Switch the glyphicon accordingly.
        let glyphicon = $("#formatDisplay .glyphicon");
        if (selectedFormatType === "video" && glyphicon.hasClass("glyphicon-music")) {
            glyphicon.removeClass("glyphicon-music").addClass("glyphicon-film");
        } else if (selectedFormatType === "audio" && glyphicon.hasClass("glyphicon-film")) {
            glyphicon.removeClass("glyphicon-film").addClass("glyphicon-music");
        }

        // Change the displayed text.
        $("#formatName").text(selectedFormat.toUpperCase());

        // Change the hidden fields.
        if ($("#formatDisplay .glyphicon-film").length > 0) {
            $("[name='VideoFormat']")[0].value = $("#formatName").text();
            $("[name='AudioFormat']")[0].value = "None";
            $("#containerVideoQuality").show();
        } else {
            $("[name='VideoFormat']")[0].value = "None";
            $("[name='AudioFormat']")[0].value = $("#formatName").text();
            $("#containerVideoQuality").hide();
        }
    });

    // SignalR code:
    let changeToPlaylistDisplay = function (task) {
        // Change the minus infront of the url and enable toggling.
        let fileEntry = $("#fileEntry_" + task);
        fileEntry.find(".glyphicon-minus").removeClass("glyphicon-minus").addClass("glyphicon-chevron-down");
        fileEntry.find("a[href='#body_" + task + "']").attr("data-toggle", "collapse");

        // Open the body by default.
        let body = $("#body_" + task);
        if (!body.hasClass("in")) {
            body.addClass("in");
        }
    }

    let changeToNormalDisplay = function (task) {
        // Change the chevron infront of the url and disable toggling.
        let fileEntry = $("#fileEntry_" + task);
        fileEntry.find(".glyphicon-chevron-down").removeClass("glyphicon-chevron-down").addClass("glyphicon-minus");
        fileEntry.find("a[href='#body_" + task + "']").attr("data-toggle", "");

        // Close the body.
        let body = $("#body_" + task);
        if (body.hasClass("in")) {
            body.removeClass("in");
        }
    }

    let createSubEntry = function (result, infoText, displayName, index) {
        // Change ids of sub entry.
        let subEntry = $("#templateFileSubEntry").clone();
        subEntry.attr("id", "fileSubEntry_" + result);
        subEntry.find(".playlistEntry").children("div").hide();
        subEntry.find("#templateContainerDownloadInfoText").attr("id", "containerDownloadInfoText_" + result).attr("hidden", false);
        subEntry.find("#templateContainerDownloadInfoProgress").attr("id", "containerDownloadInfoProgress_" + result);
        subEntry.find("#templateContainerDownloadInfoButtonDownload").attr("id", "containerDownloadInfoButtonDownload_" + result);

        // Change info in sub entry.
        let text = (index) ? index + ". " + displayName : displayName;
        subEntry.find("span").first().text(text);
        subEntry.find(".infoTextContainer").first().text(infoText);

        return subEntry;
    }

    let connection = new signalR.HubConnectionBuilder()
        .withUrl("/ws")
        .configureLogging(signalRLogLevel)
        .build();

    let start = async function () {
        try {
            await connection.start();
            console.log("connected");
        } catch (err) {
            console.log(err);

            $("#filesTextContainer").show().find("h4").text("Connection to server lost. Please reload the page.");
            $("#fileEntries").children(".panel").hide();
            allowAjax = false;
        }
    }

    start();

    connection.onclose(async () => {
        await start();
    });

    connection.on("ReceivedDownloadInfo", (outputInfo) => {
        console.log({ state: "Info received", outputInfo: outputInfo });

        let task = outputInfo.downloadTaskIdentifier;
        let result = outputInfo.downloadResultIdentifier;
        let container = $("#containerDownloadInfo_" + task);
        let infoText = "Gathering information...";

        // Info text on top must always be changed.
        container.children("div").not(".downloadDeleteButton").hide();
        container.find("#containerDownloadInfoText_" + task).show()
            .find("div[class='infoTextContainer']").text(infoText);

        changeToPlaylistDisplay(task);

        // Add sub entry to body.
        let subEntry = createSubEntry(result, infoText, outputInfo.name, outputInfo.index);
        $("#body_" + task).find(".list-group").first().append(subEntry);
    });

    connection.on("DownloadFailed", (outputInfo) => {
        console.log({ state: "Failed", outputInfo: outputInfo });

        let task = outputInfo.downloadTaskIdentifier;
        let result = outputInfo.downloadResultIdentifier;

        changeToPlaylistDisplay(task);

        // Hide the right side, hightlight the error and append the sub entry.
        let subEntry = createSubEntry(result, "", outputInfo.message);
        subEntry.find("#containerDownloadInfoText_" + result).first().hide();
        subEntry.addClass("list-group-item-danger");
        $("#body_" + task).find(".list-group").first().append(subEntry);
    });

    connection.on("DownloadStarted", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "Started", task: taskIdentifier, result: taskResultIdentifier });

        let task = taskIdentifier;
        let result = taskResultIdentifier;
        let container = $("#containerDownloadInfo_" + task);
        let subEntry = $("#body_" + task).first().find("#fileSubEntry_" + result).first();
        let infoText = "Starting download...";
        
        // Change the info text of the container and sub entry.
        container.children("div").not(".downloadDeleteButton").hide();
        container.find("#containerDownloadInfoText_" + task).show()
            .find("div[class='infoTextContainer']").text(infoText);
        subEntry.children("div").first().children("div").hide();
        subEntry.find("#containerDownloadInfoText_" + result).show()
            .find("div[class='infoTextContainer']").text(infoText);
    });

    connection.on("DownloadProgress", (taskIdentifier, resultIdentifier, percentage) => {
        console.log({ state: "Progress", task: taskIdentifier, result: resultIdentifier, percentage: percentage });

        let task = taskIdentifier;
        let result = resultIdentifier;
        let container = $("#containerDownloadInfo_" + task);
        let subEntry = $("#body_" + task).first().find("#fileSubEntry_" + result).first();

        // Change the info text of the container.
        container.children("div").not(".downloadDeleteButton").hide();
        container.find("#containerDownloadInfoText_" + task).show()
            .find("div[class='infoTextContainer']").text("Downloading...");
        // Display the progress bar in sub entry.
        subEntry.children("div").first().children("div").hide();
        subEntry.find("#containerDownloadInfoProgress_" + result).show();

        // Order of percentages might not be correct.
        let progressBar = subEntry.find(".progress-bar");
        let currentPercentage = progressBar.text().replace("%", "");

        if (percentage > currentPercentage) {
            progressBar.attr("style", "width:" + percentage + "%").text(percentage + "%");
        }
    });

    connection.on("DownloadConversion", (taskIdentifier, resultIdentifier) => {
        console.log({ state: "Conversion", task: taskIdentifier, result: resultIdentifier });

        let task = taskIdentifier;
        let result = resultIdentifier;
        let container = $("#containerDownloadInfo_" + task);
        let subEntry = $("#body_" + task).first().find("#fileSubEntry_" + result).first();
        let infoText = "Converting... (This could take a while)";

        // Change the info text of the container.
        container.children("div").not(".downloadDeleteButton").hide();
        container.find("#containerDownloadInfoText_" + task).show()
            .find("div[class='infoTextContainer']").text(infoText);
        // Change the text on the progress bar of the sub entry.
        subEntry.children("div").first().children("div").hide();
        subEntry.find("#containerDownloadInfoProgress_" + result).show();
        subEntry.find(".progress-bar").attr("style", "width:100%").text(infoText);
    });

    connection.on("DownloadResultFinished", (taskIdentifier, taskResultIdentifier) => {
        console.log({ state: "ResultFinished", task: taskIdentifier, result: taskResultIdentifier });

        let task = taskIdentifier;
        let result = taskResultIdentifier;
        let subEntry = $("#body_" + task).first().find("#fileSubEntry_" + result).first();

        // Change the download button of the sub entry accordingly.
        subEntry.children("div").first().children("div").hide();
        subEntry.find("#containerDownloadInfoButtonDownload_" + result).show()
            .find("a").attr("href", "./download?taskIdentifier=" + task + "&taskResultIdentifier=" + result);
    });

    connection.on("DownloadTaskFinished", (taskIdentifier) => {
        console.log({ state: "TaskFinished", task: taskIdentifier });

        let task = taskIdentifier;
        let container = $("#containerDownloadInfo_" + task);

        // Change the download button of the container accordingly.
        container.children("div").not(".downloadDeleteButton").hide();
        // Only show the download button if at least two elements can be downloaded.
        if ($("#body_" + task).first().find(".list-group-item").not(".list-group-item-danger").length >= 2) {
            container.find("#containerDownloadInfoButtonDownload_" + task).show()
                .find("a").attr("href", "./download?taskIdentifier=" + task);
        }
    });

    connection.on("DownloadInterrupted", (taskIdentifier) => {
        console.log({ state: "Interrupted", task: taskIdentifier });

        let task = taskIdentifier;
        let fileEntry = $("#fileEntry_" + task);
        fileEntry.addClass("panel-danger");
        fileEntry.find("#containerDownloadInfoText_" + task).show().find("div[class='infoTextContainer']").text("Download Interrupted.");
        fileEntry.find("#containerDownloadInfoText_" + task).find("img").hide();

        changeToNormalDisplay(task);
    });

    connection.on("DownloaderError", (taskIdentifier) => {
        console.log({ state: "DownloaderError", task: taskIdentifier });

        let task = taskIdentifier;
        let fileEntry = $("#fileEntry_" + task);
        fileEntry.addClass("panel-danger");
        fileEntry.find("#containerDownloadInfoText_" + task).show().find("div[class='infoTextContainer']").text("Download failed. See the logs for further information.");
        fileEntry.find("#containerDownloadInfoText_" + task).find("img").hide();

        changeToNormalDisplay(task);
    });
});