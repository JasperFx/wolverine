"use strict";

var connection = new signalR.HubConnectionBuilder().withUrl("/api/messages").build();

//Disable the send button until connection is established.
document.getElementById("sendButton").disabled = true;

connection.on("ReceiveMessage", function (json) {
    var message = JSON.parse(json);

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
    var user = document.getElementById("userInput").value;
    var text = document.getElementById("messageInput").value;
    
    var message = {type: 'chat_message', data: {'text': text, 'user': user}};
    
    connection.invoke("ReceiveMessage", JSON.stringify(message)).catch(function (err) {
        return console.error(err.toString());
    });
    event.preventDefault();
});