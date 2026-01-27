"use strict";

// Connect to the server endpoint
var connection = new signalR.HubConnectionBuilder().withUrl("/api/messages").build();

//Disable the send button until connection is established.
document.getElementById("sendButton").disabled = true;

// Receiving messages from the server
connection.on("ReceiveMessage", function (json) {
    // Note that you will need to deserialize the raw JSON
    // string
    const message = JSON.parse(json);

    // The client code will need to effectively do a logical
    // switch on the message.type. The "real" message is 
    // the data element
    if (message.type == 'ping'){
        console.log("Got ping " + message.data.number);
    }
    else{
        const li = document.createElement("li");
        document.getElementById("messagesList").appendChild(li);
        li.textContent = `${message.data.user} says ${message.data.text}`;
    }
});

connection.start().then(function () {
    document.getElementById("sendButton").disabled = false;
}).catch(function (err) {
    return console.error(err.toString());
});

document.getElementById("sendButton").addEventListener("click", function (event) {
    const user = document.getElementById("userInput").value;
    const text = document.getElementById("messageInput").value;

    // Remember that we need to wrap the raw message in this slim
    // CloudEvents wrapper
    const message = {type: 'chat_message', data: {'text': text, 'user': user}};

    // The WolverineHub method to call is ReceiveMessage with a single argument
    // for the raw JSON
    connection.invoke("ReceiveMessage", JSON.stringify(message)).catch(function (err) {
        return console.error(err.toString());
    });
    event.preventDefault();
});