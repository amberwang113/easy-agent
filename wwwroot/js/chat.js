
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
    document.getElementById('send-button').addEventListener('click', sendMessage);
    document.getElementById('reset-button').addEventListener('click', resetThread);
    updateThreadIdDisplay();
    loadHistory();
}

function handleKeyDown(event) {
    if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        sendMessage();
    }
}

function resetThread() {
    localStorage.removeItem("threadId");
    threadId = null;
    document.getElementById('chat-messages').innerHTML = '';
    updateThreadIdDisplay();
}

function sendMessage() {
    const userInput = document.getElementById('user-input').value.trim();
    if (userInput) {
        appendMessage('user-message', userInput);
        document.getElementById('user-input').value = '';
        showLoading();

        const apiUrl = `${window.location}`;

        fetch(apiUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                Content: userInput,
                SessionId: threadId
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
            if (data.sessionId) {
                threadId = data.sessionId;
                localStorage.setItem("threadId", threadId);
                updateThreadIdDisplay();
            }
            appendMessage('bot-message', data.content);
        })
        .catch(error => {
            hideLoading();
            appendMessage('bot-message', 'An error occurred while processing your request. Please try again later. Error:' + error);
        });
    }
}

function appendMessage(className, content) {
    const messagesContainer = document.getElementById('chat-messages');
    const messageDiv = document.createElement('div');
    messageDiv.className = className;
    messageDiv.innerHTML = `<p>${content}</p>`;
    messagesContainer.appendChild(messageDiv);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
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

function updateThreadIdDisplay() {
    const el = document.getElementById('thread-id');
    if (el) {
        el.textContent = threadId ? `Thread: ${threadId}` : '';
    }
}

function loadHistory() {
    if (!threadId) return;

    fetch(`${window.location.origin}/history?threadId=${encodeURIComponent(threadId)}`)
        .then(response => response.json())
        .then(data => {
            if (data.expired) {
                // Thread no longer exists in the Foundry — reset locally
                console.warn('Thread expired or not found, resetting.');
                resetThread();
                return;
            }
            if (Array.isArray(data)) {
                data.forEach(msg => {
                    // Strip enriched site-context from user messages
                    let text = msg.content;
                    const ctxIdx = text.indexOf('\n\n[Site Context:');
                    if (msg.role === 'user' && ctxIdx !== -1) {
                        text = text.substring(0, ctxIdx);
                    }
                    const className = msg.role === 'user' ? 'user-message' : 'bot-message';
                    appendMessage(className, text);
                });
            }
        })
        .catch(err => {
            console.error('Failed to load thread history:', err);
        });
}