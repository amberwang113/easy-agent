
let loadingInterval;
const loadingMessages = [
    "Analyzing your query...",
    "Processing the information...",
    "Fetching the latest insights...",
    "Compiling relevant data...",
    "Preparing a comprehensive response..."
];
let loadingIndex = 0;
let useSemanticKernel = true;
let threadId = localStorage.getItem("threadId") || null;

document.addEventListener('DOMContentLoaded', initializeChat);

function initializeChat() {
    document.getElementById('user-input').addEventListener('keydown', handleKeyDown);
    document.querySelector('.refresh-button').addEventListener('click', refreshChat);
    document.querySelector('#chat-input button').addEventListener('click', sendMessage);
}

function handleKeyDown(event) {
    if (event.key === 'Enter') {
        event.preventDefault();
        sendMessage();
    }
}

function resetThread() {
    localStorage.removeItem("threadId");
    threadId = null;
    document.getElementById('messages-container').innerHTML = '';
}

function sendMessage() {
    const userInput = document.getElementById('user-input').value.trim();
    if (userInput) {
        appendMessage('user-message', userInput);
        document.getElementById('user-input').value = '';
        showLoading();

        const hostnameWithPort = window.location.hostname + (window.location.port ? `:${window.location.port}` : '');
        const apiUrl = `https://${hostnameWithPort}/ai/chat`;

        fetch(apiUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                CustomerQuestion: userInput,
                ThreadId: threadId
            })
        })
        .then(response => {
            if (!response.ok) {
                throw new Error(`Network response was not ok (${response.status})`);
            }
            return response.json();
        })
        .then(data => {
            hideLoading();
            appendMessage('message', data.response);
            if (data.threadId) {
                threadId = data.threadId;
                localStorage.setItem("threadId", threadId);
            }
        })
        .catch(error => {
            hideLoading();
            appendMessage('message', 'An error occurred while processing your request. Please try again later. ' + error);
        });
    }
}

function appendMessage(className, content) {
    const messagesContainer = document.getElementById('messages-container');
    const messageDiv = document.createElement('div');
    messageDiv.className = className;
    messageDiv.innerHTML = `<p>${content}</p>`;
    messagesContainer.appendChild(messageDiv);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
}

function toggleMode() {
    useSemanticKernel = !useSemanticKernel;
    const button = document.getElementById('toggle-mode-button');
    resetThread();
    button.textContent = `Using: ${useSemanticKernel ? 'SK' : 'Foundry'}`;
}

function toggleChat() {
    const chatContainer = document.getElementById('chat-container');
    const chatButton = document.querySelector('.chat-button');
    if (chatContainer.style.display === 'none' || chatContainer.style.display === '') {
        chatContainer.style.display = 'flex';
        chatButton.style.display = 'none';
    } else {
        chatContainer.style.display = 'none';
        chatButton.style.display = 'block';
    }
}

function showLoading() {
    const loadingContainer = document.getElementById('loading-container');
    const loadingMessage = document.getElementById('loading-message');
    loadingContainer.style.display = 'flex';
    loadingMessage.textContent = loadingMessages[loadingIndex % loadingMessages.length];
    loadingIndex++;
    loadingInterval = setInterval(() => {
        loadingMessage.textContent = loadingMessages[loadingIndex % loadingMessages.length];
        loadingIndex++;
    }, 3000);
}

function hideLoading() {
    const loadingContainer = document.getElementById('loading-container');
    loadingContainer.style.display = 'none';
    clearInterval(loadingInterval);
}

function refreshChat() {
    const messagesContainer = document.getElementById('messages-container');
    if (messagesContainer) {
        messagesContainer.innerHTML = '';
    }
    clearInterval(loadingInterval);
}
