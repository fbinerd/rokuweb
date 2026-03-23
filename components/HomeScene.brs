sub init()
    m.defaultBridgeHost = "superweb.local:8090"
    m.bridgeHost = m.defaultBridgeHost
    m.windowEntries = []
    m.selectedIndex = 0
    m.pageStart = 0
    m.pageSize = 6
    m.gridColumns = 3
    m.isFullscreen = false
    m.isKeyboardOpen = false
    m.isClosingKeyboard = false
    m.isFullscreenRefreshInFlight = false
    m.previewRevision = 0
    m.heldDirectionKey = ""
    m.cursorX = 640
    m.cursorY = 360
    m.lockedFullscreen = false
    m.lockedWindowId = ""
    m.backHoldTimespan = invalid
    m.backLongPressThresholdMs = 650
    m.autoConnectAttempts = 0
    m.isAutoConnecting = true
    m.keyboardPurpose = ""
    deviceInfo = CreateObject("roDeviceInfo")
    m.deviceModel = deviceInfo.GetModel()
    m.firmwareVersion = deviceInfo.GetVersion()
    m.channelVersion = GetRokuChannelReleaseId()
    m.deviceId = "roku-" + m.deviceModel + "-" + m.firmwareVersion
    m.audioSessionId = ""
    m.audioMode = ""
    m.audioChunkUrl = ""
    m.audioPlayer = invalid
    m.audioUsesHls = false
    m.videoUsesStream = false
    m.videoStreamUrl = ""
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0

    m.titleLabel = m.top.findNode("titleLabel")
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.versionLabel = m.top.findNode("versionLabel")
    m.fullscreenPosterA = m.top.findNode("fullscreenPosterA")
    m.fullscreenPosterB = m.top.findNode("fullscreenPosterB")
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.cursorMarker = m.top.findNode("cursorMarker")
    m.bridgeRequestTask = m.top.findNode("bridgeRequestTask")
    m.inputLogTask = m.top.findNode("inputLogTask")
    m.controlTask = m.top.findNode("controlTask")
    m.clickControlTask = m.top.findNode("clickControlTask")
    m.textControlTask = m.top.findNode("textControlTask")
    m.panelRefreshTimer = m.top.findNode("panelRefreshTimer")
    m.previewRefreshTimer = m.top.findNode("previewRefreshTimer")
    m.editableActivationTimer = m.top.findNode("editableActivationTimer")
    m.autoConnectTimer = m.top.findNode("autoConnectTimer")
    m.fullscreenStreamTimer = m.top.findNode("fullscreenStreamTimer")
    m.fullscreenVideoWatchTimer = m.top.findNode("fullscreenVideoWatchTimer")
    m.cursorMoveTimer = m.top.findNode("cursorMoveTimer")
    m.audioRetryTimer = m.top.findNode("audioRetryTimer")
    m.audioFallbackTimer = m.top.findNode("audioFallbackTimer")
    m.audioHlsRestartTimer = m.top.findNode("audioHlsRestartTimer")
    m.panelAudioNode = m.top.findNode("panelAudioNode")
    m.panelAudioVideo = m.top.findNode("panelAudioVideo")
    m.fullscreenVideo = m.top.findNode("fullscreenVideo")

    m.panelGroups = [
        m.top.findNode("panel0")
        m.top.findNode("panel1")
        m.top.findNode("panel2")
        m.top.findNode("panel3")
        m.top.findNode("panel4")
        m.top.findNode("panel5")
    ]

    m.panelFrames = [
        m.top.findNode("panelFrame0")
        m.top.findNode("panelFrame1")
        m.top.findNode("panelFrame2")
        m.top.findNode("panelFrame3")
        m.top.findNode("panelFrame4")
        m.top.findNode("panelFrame5")
    ]

    m.panelPosters = [
        m.top.findNode("panelPoster0")
        m.top.findNode("panelPoster1")
        m.top.findNode("panelPoster2")
        m.top.findNode("panelPoster3")
        m.top.findNode("panelPoster4")
        m.top.findNode("panelPoster5")
    ]

    m.panelLabels = [
        m.top.findNode("panelLabel0")
        m.top.findNode("panelLabel1")
        m.top.findNode("panelLabel2")
        m.top.findNode("panelLabel3")
        m.top.findNode("panelLabel4")
        m.top.findNode("panelLabel5")
    ]

    m.bridgeRequestTask.observeField("responseCode", "onBridgeResponseCodeChanged")
    m.panelRefreshTimer.observeField("fire", "onPanelRefreshTimerFire")
    m.previewRefreshTimer.observeField("fire", "onPreviewRefreshTimerFire")
    m.autoConnectTimer.observeField("fire", "onAutoConnectTimerFire")
    m.fullscreenStreamTimer.observeField("fire", "onFullscreenStreamTimerFire")
    m.fullscreenVideoWatchTimer.observeField("fire", "onFullscreenVideoWatchTimerFire")
    m.cursorMoveTimer.observeField("fire", "onCursorMoveTimerFire")
    m.audioRetryTimer.observeField("fire", "onAudioRetryTimerFire")
    m.audioFallbackTimer.observeField("fire", "onAudioFallbackTimerFire")
    m.audioHlsRestartTimer.observeField("fire", "onAudioHlsRestartTimerFire")
    m.fullscreenPosterA.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    m.fullscreenPosterB.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    m.clickControlTask.observeField("completedToken", "onClickControlTaskCompleted")
    m.textControlTask.observeField("completedToken", "onTextControlTaskCompleted")
    if m.editableActivationTimer <> invalid
        m.editableActivationTimer.observeField("fire", "onEditableActivationTimerFire")
    end if
    if m.panelAudioNode <> invalid
        m.panelAudioNode.observeField("state", "onPanelAudioStateChanged")
    end if
    if m.panelAudioVideo <> invalid
        m.panelAudioVideo.observeField("state", "onPanelAudioVideoStateChanged")
    end if
    if m.fullscreenVideo <> invalid
        m.fullscreenVideo.observeField("state", "onFullscreenVideoStateChanged")
    end if
    m.top.setFocus(true)
    m.pendingEditableActivation = false
    m.pendingEditableValue = ""
    m.pendingEditableMultiline = false

    m.titleLabel.text = GetRokuAppShortName()
    m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
    m.subtitleLabel.text = "Procurando automaticamente o super..."
    m.versionLabel.text = "Canal " + GetRokuChannelName() + " | " + GetRokuChannelReleaseId()
    hideGrid()
    beginAutoConnect()
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    normalizedKey = LCase(getString(key, ""))

    if m.isKeyboardOpen
        return false
    end if

    if m.isFullscreen
        if not press
            if normalizedKey = "back"
                heldMs = 0
                if m.backHoldTimespan <> invalid
                    heldMs = m.backHoldTimespan.TotalMilliseconds()
                end if
                m.backHoldTimespan = invalid

                if heldMs >= m.backLongPressThresholdMs
                    if m.lockedFullscreen
                        autoFullscreenIndex = findAutoOpenFullscreenIndex()
                        if autoFullscreenIndex < 0
                            m.lockedFullscreen = false
                            m.lockedWindowId = ""
                        end if
                    end if
                    if m.lockedFullscreen
                        return true
                    end if
                    hideFullscreen()
                    return true
                end if

                sendRemoteCommand("history-back")
                scheduleFullscreenRefresh()
                return true
            end if

            if normalizedKey = m.heldDirectionKey
                stopHeldDirection()
                return true
            end if

            return false
        end if

        reportInputKey(normalizedKey)

        if normalizedKey = "back"
            m.backHoldTimespan = CreateObject("roTimespan")
            m.backHoldTimespan.Mark()
            return true
        end if

        if normalizedKey = "ok"
            if m.pendingEditableActivation
                openKeyboardDialog(m.pendingEditableValue, m.pendingEditableMultiline)
                clearPendingEditableActivation()
                return true
            end if
            sendClickCommand()
            return true
        end if

        if isInstantReplayKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("reload")
            scheduleFullscreenRefresh()
            return true
        end if

        if isMinusKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("history-back")
            scheduleFullscreenRefresh()
            return true
        end if

        if isPlusKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("history-forward")
            scheduleFullscreenRefresh()
            return true
        end if

        if isOptionsKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("enter")
            scheduleFullscreenRefresh()
            return true
        end if

        if isRevKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("media-seek-backward")
            scheduleFullscreenRefresh()
            return true
        end if

        if isFwdKey(normalizedKey)
            clearPendingEditableActivation()
            sendRemoteCommand("media-seek-forward")
            scheduleFullscreenRefresh()
            return true
        end if

        if normalizedKey = "up" or normalizedKey = "down" or normalizedKey = "left" or normalizedKey = "right"
            clearPendingEditableActivation()
            startHeldDirection(normalizedKey)
            return true
        end if

        if normalizedKey = "play"
            clearPendingEditableActivation()
            sendRemoteCommand("media-play-pause")
            scheduleFullscreenRefresh()
            return true
        end if

        return false
    end if

    if not press
        return false
    end if

    reportInputKey(normalizedKey)

    if normalizedKey = "up"
        moveSelection(-m.gridColumns)
        return true
    end if

    if normalizedKey = "down"
        moveSelection(m.gridColumns)
        return true
    end if

    if normalizedKey = "left"
        moveSelection(-1)
        return true
    end if

    if normalizedKey = "right"
        moveSelection(1)
        return true
    end if

    if normalizedKey = "ok"
        if m.windowEntries.Count() > 0
            showFullscreen()
        else
            if m.isAutoConnecting
                loadWindows()
            else
                promptForBridgeHost()
            end if
        end if
        return true
    end if

    if normalizedKey = "play"
        loadWindows()
        return true
    end if

    return false
end function

sub reportInputKey(key as string)
    if m.inputLogTask = invalid
        return
    end if

    m.inputLogTask.bridgeHost = m.bridgeHost
    m.inputLogTask.keyName = key
    m.inputLogTask.fullscreen = m.isFullscreen
    m.inputLogTask.selectedIndex = m.selectedIndex
    m.inputLogTask.deviceId = m.deviceId
    m.inputLogTask.deviceModel = m.deviceModel
    m.inputLogTask.firmwareVersion = m.firmwareVersion
    m.inputLogTask.channelVersion = m.channelVersion
    m.inputLogTask.control = "RUN"
end sub

sub loadWindows(silent = false as boolean)
    if not silent and m.isAutoConnecting
        m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
        m.subtitleLabel.text = "Tentativa " + (m.autoConnectAttempts + 1).ToStr() + " em andamento. Tentando automaticamente..."
    else if not silent
        m.statusLabel.text = "Consultando bridge em " + m.bridgeHost
        m.subtitleLabel.text = "Aguarde a resposta do servidor"
    end if
    m.bridgeRequestTask.responseCode = -1
    m.bridgeRequestTask.responseBody = ""
    m.bridgeRequestTask.errorMessage = ""
    m.bridgeRequestTask.bridgeHost = m.bridgeHost
    m.bridgeRequestTask.control = "RUN"
end sub

sub onBridgeResponseCodeChanged()
    if m.bridgeRequestTask.responseCode < 0
        return
    end if

    applyBridgeResponse()
end sub

sub applyBridgeResponse()
    responseBody = m.bridgeRequestTask.responseBody
    responseCode = m.bridgeRequestTask.responseCode

    if responseCode <> 200 or responseBody = invalid or responseBody = ""
        if m.isAutoConnecting
            m.autoConnectAttempts = m.autoConnectAttempts + 1
            m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
            m.subtitleLabel.text = "Ainda nao foi possivel conectar. Tentando novamente automaticamente..."
            scheduleAutoConnectRetry()
        else
            startPanelRefresh()
            m.statusLabel.text = "Falha ao conectar em " + m.bridgeHost
            m.subtitleLabel.text = "Tentando reconectar automaticamente..."
        end if
        hideGrid()
        return
    end if

    json = ParseJson(responseBody)
    if json = invalid or json.windows = invalid
        m.statusLabel.text = "Resposta invalida do bridge HTTP"
        m.subtitleLabel.text = "O endpoint /api/windows nao retornou um JSON esperado."
        hideGrid()
        return
    end if

    activeSessionName = ""
    if json.activeSessions <> invalid and json.activeSessions.Count() > 0
        activeSessionName = getString(json.activeSessions[0].name, "")
    end if

    previousSelectedId = ""
    if m.windowEntries.Count() > 0 and m.selectedIndex >= 0 and m.selectedIndex < m.windowEntries.Count()
        previousSelectedId = getString(m.windowEntries[m.selectedIndex].id, "")
    end if

    m.windowEntries = []

    for each window in json.windows
        m.windowEntries.Push({
            id: getString(window.id, "")
            title: getString(window.title, "Janela sem titulo")
            state: getString(window.state, "Desconhecido")
            thumbnailUrl: getString(window.thumbnailUrl, "")
            streamUrl: getString(window.streamUrl, "")
            initialUrl: getString(window.initialUrl, "")
            audioStreamUrl: getString(window.audioStreamUrl, "")
            audioAvailable: getBool(window.audioAvailable, false)
            autoOpenFullscreen: getBool(window.autoOpenFullscreen, false)
        })
    end for

    if m.windowEntries.Count() = 0
        m.statusLabel.text = "Bridge conectado, sem paineis disponiveis"
        m.subtitleLabel.text = "Nenhuma janela publicada ainda. Atualizando automaticamente..."
        startPanelRefresh()
        hideGrid()
        return
    end if

    m.selectedIndex = 0
    if previousSelectedId <> ""
        for i = 0 to m.windowEntries.Count() - 1
            if getString(m.windowEntries[i].id, "") = previousSelectedId
                m.selectedIndex = i
                exit for
            end if
        end for
    end if

    autoFullscreenIndex = findAutoOpenFullscreenIndex()
    if autoFullscreenIndex >= 0
        m.selectedIndex = autoFullscreenIndex
        m.lockedFullscreen = true
        m.lockedWindowId = getString(m.windowEntries[autoFullscreenIndex].id, "")
    else
        m.lockedFullscreen = false
        m.lockedWindowId = ""
    end if

    m.pageStart = 0

    if activeSessionName <> ""
        m.statusLabel.text = "Sessao ativa: " + activeSessionName
    else
        m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
    end if
    m.isAutoConnecting = false
    startPanelRefresh()
    refreshGrid()
    if not m.isFullscreen and autoFullscreenIndex >= 0
        m.selectedIndex = autoFullscreenIndex
        showFullscreen()
        return
    end if
    m.top.setFocus(true)
end sub

function findAutoOpenFullscreenIndex() as integer
    if m.windowEntries = invalid or m.windowEntries.Count() = 0
        return -1
    end if

    for i = 0 to m.windowEntries.Count() - 1
        if getBool(m.windowEntries[i].autoOpenFullscreen, false)
            return i
        end if
    end for

    return -1
end function

sub beginAutoConnect()
    m.autoConnectAttempts = 0
    m.isAutoConnecting = true
    startPanelRefresh()
    loadWindows()
end sub

sub startPanelRefresh()
    if m.panelRefreshTimer = invalid
        return
    end if

    m.panelRefreshTimer.control = "stop"
    m.panelRefreshTimer.control = "start"
end sub

sub stopPanelRefresh()
    if m.panelRefreshTimer = invalid
        return
    end if

    m.panelRefreshTimer.control = "stop"
end sub

sub onPanelRefreshTimerFire()
    if m.isKeyboardOpen or m.isFullscreen
        return
    end if

    loadWindows(true)
end sub

sub scheduleAutoConnectRetry()
    if m.autoConnectTimer = invalid
        loadWindows()
        return
    end if

    m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
    m.subtitleLabel.text = "Tentativa " + m.autoConnectAttempts.ToStr() + " falhou. Tentando novamente automaticamente..."
    m.autoConnectTimer.control = "stop"
    m.autoConnectTimer.control = "start"
end sub

sub onAutoConnectTimerFire()
    if m.isAutoConnecting
        loadWindows()
    end if
end sub

sub refreshGrid()
    if m.windowEntries.Count() = 0
        hideGrid()
        return
    end if

    m.pageStart = int(m.selectedIndex / m.pageSize) * m.pageSize
    visibleEnd = m.pageStart + m.pageSize - 1
    if visibleEnd >= m.windowEntries.Count()
        visibleEnd = m.windowEntries.Count() - 1
    end if

    selectedEntry = m.windowEntries[m.selectedIndex]
    m.subtitleLabel.text = "Painel " + (m.selectedIndex + 1).ToStr() + " de " + m.windowEntries.Count().ToStr() + " | Setas para navegar | OK para expandir"
    m.statusLabel.text = "Selecionado: " + selectedEntry.title

    for slot = 0 to m.pageSize - 1
        absoluteIndex = m.pageStart + slot
        group = m.panelGroups[slot]
        frame = m.panelFrames[slot]
        poster = m.panelPosters[slot]
        label = m.panelLabels[slot]

        if absoluteIndex >= m.windowEntries.Count()
            group.visible = false
        else
            entry = m.windowEntries[absoluteIndex]
            group.visible = true
            poster.uri = appendCacheBust(entry.thumbnailUrl)
            label.text = trimTitle(entry.title)

            if absoluteIndex = m.selectedIndex
                frame.color = "0x3B82F6FF"
            else
                frame.color = "0x334155FF"
            end if
        end if
    end for
end sub

sub hideGrid()
    for each group in m.panelGroups
        group.visible = false
    end for
end sub

sub moveSelection(offset as integer)
    if m.windowEntries.Count() = 0
        return
    end if

    nextIndex = m.selectedIndex + offset
    if nextIndex < 0
        nextIndex = 0
    else if nextIndex >= m.windowEntries.Count()
        nextIndex = m.windowEntries.Count() - 1
    end if

    if nextIndex = m.selectedIndex
        return
    end if

    m.selectedIndex = nextIndex
    refreshGrid()
end sub

sub showFullscreen()
    if m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl = invalid or entry.thumbnailUrl = ""
        m.statusLabel.text = "Preview indisponivel para o painel selecionado"
        return
    end if

    m.isFullscreen = true
    clearPendingEditableActivation()
    m.cursorX = 640
    m.cursorY = 360
    hideGrid()
    m.titleLabel.visible = false
    m.statusLabel.visible = false
    m.subtitleLabel.visible = false
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.videoUsesStream = Instr(1, LCase(getString(entry.streamUrl, "")), ".m3u8") > 0
    m.videoStreamUrl = getString(entry.streamUrl, "")
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
    if m.videoUsesStream and m.fullscreenVideo <> invalid
        content = CreateObject("roSGNode", "ContentNode")
        content.url = appendCacheBust(m.videoStreamUrl)
        content.streamFormat = "hls"
        content.title = getString(entry.title, "Painel")
        ? "[HLS] play => "; content.url
        m.fullscreenVideo.content = content
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.visible = true
        m.fullscreenVideo.control = "play"
        m.activeFullscreenPoster.visible = false
        m.bufferFullscreenPoster.visible = false
        m.statusLabel.text = "Iniciando stream HLS do painel..."
    else
        m.activeFullscreenPoster.uri = appendCacheBust(entry.thumbnailUrl)
        m.activeFullscreenPoster.visible = true
        m.bufferFullscreenPoster.visible = false
        m.bufferFullscreenPoster.uri = ""
    end if
    m.cursorMarker.visible = true
    m.isFullscreenRefreshInFlight = false
    stopPanelRefresh()
    updateCursorMarker()
    if not m.videoUsesStream
        startPanelAudio(entry)
    end if
    startFullscreenStream()
    m.top.setFocus(true)
end sub

sub hideFullscreen()
    if m.lockedFullscreen
        return
    end if

    m.isFullscreen = false
    clearPendingEditableActivation()
    stopHeldDirection()
    stopFullscreenStream()
    stopPanelAudio()
    m.videoUsesStream = false
    m.videoStreamUrl = ""
    if m.fullscreenVideo <> invalid
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.content = invalid
        m.fullscreenVideo.visible = false
    end if
    if m.fullscreenVideoWatchTimer <> invalid
        m.fullscreenVideoWatchTimer.control = "stop"
    end if
    m.fullscreenPosterA.visible = false
    m.fullscreenPosterB.visible = false
    m.cursorMarker.visible = false
    m.titleLabel.visible = true
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    startPanelRefresh()
    refreshGrid()
    m.top.setFocus(true)
end sub

sub startPanelAudio(entry as object)
    stopPanelAudio()

    if m.panelAudioNode = invalid
        return
    end if

    if entry = invalid
        return
    end if

    audioUrl = getString(entry.audioStreamUrl, "")
    if audioUrl = ""
        return
    end if

    m.audioUsesHls = Instr(1, LCase(audioUrl), ".m3u8") > 0
    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(audioUrl)
    if m.audioUsesHls
        content.streamFormat = "hls"
    else
        content.streamFormat = "wav"
    end if
    content.title = getString(entry.title, "Audio do painel")
    m.audioSessionId = getString(entry.id, "")
    m.audioChunkUrl = audioUrl
    m.audioMode = "scenegraph"
    if m.audioUsesHls and m.panelAudioVideo <> invalid
        m.panelAudioVideo.content = content
        m.panelAudioVideo.control = "stop"
        m.panelAudioVideo.control = "play"
        m.statusLabel.text = "Iniciando audio HLS do painel..."
    else
        m.panelAudioNode.content = content
        m.panelAudioNode.control = "stop"
        m.panelAudioNode.control = "play"
    end if
    if m.audioFallbackTimer <> invalid
        m.audioFallbackTimer.control = "stop"
        m.audioFallbackTimer.control = "start"
    end if
end sub

sub onFullscreenVideoStateChanged()
    if m.fullscreenVideo = invalid or not m.videoUsesStream
        return
    end if

    state = LCase(getString(m.fullscreenVideo.state, ""))
    if state = ""
        return
    end if

    ? "[HLS] video state => "; state

    if state = "playing"
        m.fullscreenVideoStallCount = 0
        m.statusLabel.text = "Stream HLS do painel em reproducao"
    else if state = "buffering"
        m.statusLabel.text = "Bufferizando stream HLS do painel..."
    else if state = "error"
        m.statusLabel.text = "Falha no stream HLS; mantendo preview por snapshots"
    else if state = "finished" or state = "stopped"
        if m.isFullscreen and m.videoUsesStream and m.videoStreamUrl <> ""
            content = CreateObject("roSGNode", "ContentNode")
            content.url = appendCacheBust(m.videoStreamUrl)
            content.streamFormat = "hls"
            content.title = "Painel"
            m.fullscreenVideo.content = content
            m.fullscreenVideo.control = "stop"
            m.fullscreenVideo.control = "play"
            m.statusLabel.text = "Reiniciando stream HLS do painel..."
        end if
    end if
end sub

sub onFullscreenVideoWatchTimerFire()
    return
end sub

sub stopPanelAudio()
    if m.panelAudioNode = invalid
        return
    end if

    m.audioSessionId = ""
    m.audioMode = ""
    m.audioChunkUrl = ""
    if m.audioRetryTimer <> invalid
        m.audioRetryTimer.control = "stop"
    end if
    if m.audioFallbackTimer <> invalid
        m.audioFallbackTimer.control = "stop"
    end if
    if m.audioHlsRestartTimer <> invalid
        m.audioHlsRestartTimer.control = "stop"
    end if
    m.audioUsesHls = false
    if m.panelAudioNode <> invalid
        m.panelAudioNode.control = "stop"
        m.panelAudioNode.content = invalid
    end if
    if m.panelAudioVideo <> invalid
        m.panelAudioVideo.control = "stop"
        m.panelAudioVideo.content = invalid
    end if
    if m.audioPlayer <> invalid
        m.audioPlayer.Stop()
    end if
end sub

sub onPanelAudioStateChanged()
    if m.panelAudioNode = invalid or m.audioUsesHls
        return
    end if

    state = LCase(getString(m.panelAudioNode.state, ""))
    if state = ""
        return
    end if

    if state = "playing"
        m.audioMode = "scenegraph"
        if m.audioFallbackTimer <> invalid
            m.audioFallbackTimer.control = "stop"
        end if
        m.statusLabel.text = "Audio do painel em reproducao"
    else if state = "buffering"
        m.statusLabel.text = "Bufferizando audio do painel..."
    else if state = "stopped"
        if m.isFullscreen and m.audioSessionId <> ""
            m.statusLabel.text = "Audio do painel parado"
            scheduleAudioRetry()
        end if
    else if state = "error"
        if m.isFullscreen and m.audioSessionId <> ""
            m.statusLabel.text = "Falha ao reproduzir audio do painel"
            scheduleAudioRetry()
        end if
    end if
end sub

sub onPanelAudioVideoStateChanged()
    if m.panelAudioVideo = invalid or not m.audioUsesHls
        return
    end if

    state = LCase(getString(m.panelAudioVideo.state, ""))
    if state = ""
        return
    end if

    if state = "playing"
        m.audioMode = "scenegraph"
        if m.audioFallbackTimer <> invalid
            m.audioFallbackTimer.control = "stop"
        end if
        m.statusLabel.text = "Audio HLS do painel em reproducao"
    else if state = "buffering"
        m.statusLabel.text = "Bufferizando audio HLS..."
    else if state = "stopped" or state = "error" or state = "finished"
        if m.isFullscreen and m.audioSessionId <> ""
            m.statusLabel.text = "Reiniciando audio HLS do painel..."
            scheduleAudioHlsRestart()
        end if
    end if
end sub

sub scheduleAudioHlsRestart()
    if m.audioHlsRestartTimer = invalid
        restartPanelAudio()
        return
    end if

    m.audioHlsRestartTimer.control = "stop"
    m.audioHlsRestartTimer.control = "start"
end sub

sub onAudioHlsRestartTimerFire()
    if not m.audioUsesHls
        return
    end if

    restartPanelAudio()
end sub

sub scheduleAudioRetry()
    if m.audioRetryTimer = invalid
        restartPanelAudio()
        return
    end if

    m.audioRetryTimer.control = "stop"
    m.audioRetryTimer.control = "start"
end sub

sub onAudioRetryTimerFire()
    if m.audioMode = "legacy"
        playLegacyPanelAudioChunk()
        return
    end if

    if m.audioUsesHls
        restartPanelAudio()
        return
    end if

    restartPanelAudio()
end sub

sub restartPanelAudio()
    if not m.isFullscreen or m.audioSessionId = ""
        return
    end if

    if m.selectedIndex < 0 or m.selectedIndex >= m.windowEntries.Count()
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if getString(entry.id, "") <> m.audioSessionId
        return
    end if

    startPanelAudio(entry)
end sub

sub onAudioFallbackTimerFire()
    if not m.isFullscreen or m.audioSessionId = ""
        return
    end if

    if m.audioUsesHls and m.panelAudioVideo <> invalid
        state = LCase(getString(m.panelAudioVideo.state, ""))
        if state = "playing"
            return
        end if
    end if

    if m.audioMode = "scenegraph" and not m.audioUsesHls and m.panelAudioNode <> invalid
        state = LCase(getString(m.panelAudioNode.state, ""))
        if state = "playing"
            return
        end if
    end if

    m.statusLabel.text = "Tentando audio legado do painel..."
    startLegacyPanelAudio()
end sub

sub startLegacyPanelAudio()
    if m.audioChunkUrl = ""
        return
    end if

    m.audioMode = "legacy"
    m.audioUsesHls = false
    if m.panelAudioNode <> invalid
        m.panelAudioNode.control = "stop"
        m.panelAudioNode.content = invalid
    end if
    if m.panelAudioVideo <> invalid
        m.panelAudioVideo.control = "stop"
        m.panelAudioVideo.content = invalid
    end if

    playLegacyPanelAudioChunk()
end sub

sub playLegacyPanelAudioChunk()
    if not m.isFullscreen or m.audioSessionId = "" or m.audioChunkUrl = ""
        return
    end if

    audioItem = CreateObject("roAssociativeArray")
    audioItem.url = appendCacheBust(m.audioChunkUrl)
    audioItem.streamformat = "wav"
    audioItem.title = "Audio do painel"

    if m.audioPlayer <> invalid
        m.audioPlayer.Stop()
    end if
    m.audioPlayer = CreateObject("roAudioPlayer")
    m.audioPlayer.SetLoop(false)
    m.audioPlayer.AddContent(audioItem)
    m.audioPlayer.Play()
    m.statusLabel.text = "Audio legado do painel em reproducao"

    if m.audioRetryTimer <> invalid
        m.audioRetryTimer.control = "stop"
        m.audioRetryTimer.control = "start"
    end if
end sub

sub moveCursor(command as string)
    stepSize = 8

    if command = "up"
        m.cursorY = m.cursorY - stepSize
    else if command = "down"
        m.cursorY = m.cursorY + stepSize
    else if command = "left"
        m.cursorX = m.cursorX - stepSize
    else if command = "right"
        m.cursorX = m.cursorX + stepSize
    end if

    if m.cursorX < 0
        m.cursorX = 0
    else if m.cursorX > 1279
        m.cursorX = 1279
    end if

    if m.cursorY < 0
        m.cursorY = 0
    else if m.cursorY > 719
        m.cursorY = 719
    end if

    updateCursorMarker()
end sub

sub startHeldDirection(key as string)
    m.heldDirectionKey = key
    moveCursor(key)
    sendPointerCommand("move")

    if m.cursorMoveTimer <> invalid
        m.cursorMoveTimer.control = "stop"
        m.cursorMoveTimer.control = "start"
    end if
end sub

sub stopHeldDirection()
    m.heldDirectionKey = ""
    if m.cursorMoveTimer <> invalid
        m.cursorMoveTimer.control = "stop"
    end if
end sub

sub onCursorMoveTimerFire()
    if not m.isFullscreen
        stopHeldDirection()
        return
    end if

    if m.heldDirectionKey = ""
        return
    end if

    moveCursor(m.heldDirectionKey)
    sendPointerCommand("move")
end sub

sub updateCursorMarker()
    m.cursorMarker.translation = [m.cursorX - 7, m.cursorY - 5]
end sub

sub sendRemoteCommand(command as string)
    if m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.id = invalid or entry.id = ""
        return
    end if

    if m.controlTask = invalid
        return
    end if

    m.controlTask.bridgeHost = m.bridgeHost
    m.controlTask.windowId = entry.id
    m.controlTask.command = command
    m.controlTask.cursorX = m.cursorX
    m.controlTask.cursorY = m.cursorY
    m.controlTask.textValue = ""
    m.controlTask.control = "RUN"
end sub

sub sendPointerCommand(command as string)
    if m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.id = invalid or entry.id = ""
        return
    end if

    if m.controlTask = invalid
        return
    end if

    m.controlTask.bridgeHost = m.bridgeHost
    m.controlTask.windowId = entry.id
    m.controlTask.command = command
    m.controlTask.cursorX = m.cursorX
    m.controlTask.cursorY = m.cursorY
    m.controlTask.textValue = ""
    m.controlTask.control = "RUN"
end sub

sub sendClickCommand()
    if m.windowEntries.Count() = 0 or m.clickControlTask = invalid
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.id = invalid or entry.id = ""
        return
    end if

    m.clickControlTask.bridgeHost = m.bridgeHost
    m.clickControlTask.windowId = entry.id
    m.clickControlTask.command = "click"
    m.clickControlTask.cursorX = m.cursorX
    m.clickControlTask.cursorY = m.cursorY
    m.clickControlTask.textValue = ""
    m.clickControlTask.responseCode = 0
    m.clickControlTask.responseBody = ""
    m.clickControlTask.control = "RUN"
end sub

sub sendTextCommand(textValue as string)
    if m.windowEntries.Count() = 0 or m.textControlTask = invalid
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.id = invalid or entry.id = ""
        return
    end if

    m.textControlTask.bridgeHost = m.bridgeHost
    m.textControlTask.windowId = entry.id
    m.textControlTask.command = "set-text"
    m.textControlTask.cursorX = m.cursorX
    m.textControlTask.cursorY = m.cursorY
    m.textControlTask.textValue = textValue
    m.textControlTask.responseCode = 0
    m.textControlTask.responseBody = ""
    m.textControlTask.control = "RUN"
end sub

sub refreshFullscreenPreview()
    if not m.isFullscreen or m.windowEntries.Count() = 0 or m.isFullscreenRefreshInFlight
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl <> invalid and entry.thumbnailUrl <> ""
        m.isFullscreenRefreshInFlight = true
        m.bufferFullscreenPoster.uri = appendCacheBust(entry.thumbnailUrl)
    end if
end sub

sub scheduleFullscreenRefresh()
    if m.previewRefreshTimer = invalid
        refreshFullscreenPreview()
        return
    end if

    m.previewRefreshTimer.control = "stop"
    m.previewRefreshTimer.control = "start"
end sub

sub onPreviewRefreshTimerFire()
    refreshFullscreenPreview()
end sub

sub onFullscreenStreamTimerFire()
    loadWindows(true)
    refreshFullscreenPreview()
end sub

sub onClickControlTaskCompleted()
    if m.clickControlTask = invalid
        return
    end if

    if m.clickControlTask.responseBody = invalid or m.clickControlTask.responseBody = ""
        scheduleFullscreenRefresh()
        return
    end if

    result = ParseJson(m.clickControlTask.responseBody)
    scheduleFullscreenRefresh()

    if result = invalid
        return
    end if

    if result.editable = true
        armPendingEditableActivation(getString(result.value, ""), result.multiline = true)
    else
        clearPendingEditableActivation()
    end if
end sub

sub onTextControlTaskCompleted()
    closeKeyboardDialog()
    scheduleFullscreenRefresh()
end sub

sub openKeyboardDialog(initialValue as string, multiline as boolean)
    if m.isKeyboardOpen
        return
    end if

    keyboard = CreateObject("roSGNode", "KeyboardDialog")
    if keyboard = invalid
        m.statusLabel.text = "Teclado virtual indisponivel nesta Roku"
        return
    end if

    keyboard.title = "Digite no campo"
    keyboard.text = initialValue
    keyboard.buttons = ["Enviar", "Cancelar"]
    keyboard.observeField("buttonSelected", "onKeyboardDialogButtonSelected")
    keyboard.observeField("wasClosed", "onKeyboardDialogWasClosed")

    m.keyboardDialog = keyboard
    m.isKeyboardOpen = true
    m.top.dialog = keyboard
end sub

sub promptForBridgeHost()
    if m.isKeyboardOpen
        return
    end if

    keyboard = CreateObject("roSGNode", "KeyboardDialog")
    if keyboard = invalid
        m.statusLabel.text = "Entrada de IP indisponivel nesta Roku"
        return
    end if

    keyboard.title = "IP do super"
    keyboard.text = m.bridgeHost
    keyboard.buttons = ["Conectar", "Cancelar"]
    keyboard.observeField("buttonSelected", "onKeyboardDialogButtonSelected")
    keyboard.observeField("wasClosed", "onKeyboardDialogWasClosed")

    m.keyboardPurpose = "bridge-host"
    m.keyboardDialog = keyboard
    m.isKeyboardOpen = true
    m.top.dialog = keyboard
end sub

sub closeKeyboardDialog()
    if m.isClosingKeyboard
        return
    end if

    m.isClosingKeyboard = true

    if m.keyboardDialog <> invalid
        m.keyboardDialog.unobserveField("buttonSelected")
        m.keyboardDialog.unobserveField("wasClosed")
    end if

    m.keyboardDialog = invalid
    m.isKeyboardOpen = false
    m.keyboardPurpose = ""
    m.top.dialog = invalid
    if m.isFullscreen
        m.top.setFocus(true)
    end if
    m.isClosingKeyboard = false
end sub

sub armPendingEditableActivation(initialValue as string, multiline as boolean)
    m.pendingEditableActivation = true
    m.pendingEditableValue = initialValue
    m.pendingEditableMultiline = multiline
    m.statusLabel.text = "Pressione OK novamente para digitar"
    if m.editableActivationTimer <> invalid
        m.editableActivationTimer.control = "stop"
        m.editableActivationTimer.control = "start"
    end if
end sub

sub clearPendingEditableActivation()
    m.pendingEditableActivation = false
    m.pendingEditableValue = ""
    m.pendingEditableMultiline = false
    if m.editableActivationTimer <> invalid
        m.editableActivationTimer.control = "stop"
    end if
end sub

sub onEditableActivationTimerFire()
    clearPendingEditableActivation()
end sub

sub onKeyboardDialogButtonSelected()
    if m.keyboardDialog = invalid
        return
    end if

    selectedButton = m.keyboardDialog.buttonSelected
    purpose = m.keyboardPurpose
    enteredText = ""
    if m.keyboardDialog.text <> invalid
        enteredText = m.keyboardDialog.text
    end if

    closeKeyboardDialog()

    if selectedButton = 0
        if purpose = "bridge-host"
            m.bridgeHost = normalizeBridgeHost(enteredText)
            m.statusLabel.text = "Conectando em " + m.bridgeHost
            m.subtitleLabel.text = "Tentativa manual iniciada"
            loadWindows()
        else
            sendTextCommand(enteredText)
        end if
    end if
end sub

sub onKeyboardDialogWasClosed()
    closeKeyboardDialog()
end sub

sub onBufferPosterLoadStatusChanged()
    if not m.isFullscreen
        return
    end if

    if m.bufferFullscreenPoster = invalid or m.activeFullscreenPoster = invalid
        return
    end if

    if m.bufferFullscreenPoster.loadStatus = "failed"
        m.isFullscreenRefreshInFlight = false
        return
    end if

    if m.bufferFullscreenPoster.loadStatus <> "ready"
        return
    end if

    m.bufferFullscreenPoster.visible = true
    m.activeFullscreenPoster.visible = false

    previousActive = m.activeFullscreenPoster
    m.activeFullscreenPoster = m.bufferFullscreenPoster
    m.bufferFullscreenPoster = previousActive
    m.bufferFullscreenPoster.visible = false
    m.isFullscreenRefreshInFlight = false
end sub

sub startFullscreenStream()
    if m.fullscreenStreamTimer = invalid
        return
    end if

    m.fullscreenStreamTimer.control = "stop"
    m.fullscreenStreamTimer.control = "start"
end sub

sub stopFullscreenStream()
    m.isFullscreenRefreshInFlight = false
    if m.fullscreenStreamTimer <> invalid
        m.fullscreenStreamTimer.control = "stop"
    end if
end sub

function getString(value as dynamic, fallback as string) as string
    if value = invalid
        return fallback
    end if

    if Type(value) = "roString" or Type(value) = "String"
        return value
    end if

    return fallback
end function

function getBool(value as dynamic, fallback as boolean) as boolean
    if value = invalid
        return fallback
    end if

    if value = true
        return true
    end if

    if value = false
        return false
    end if

    return fallback
end function

function appendCacheBust(url as string) as string
    if url = invalid or url = ""
        return ""
    end if

    separator = "?"
    if Instr(1, url, "?") > 0
        separator = "&"
    end if

    m.previewRevision = m.previewRevision + 1
    return url + separator + "ts=" + m.previewRevision.ToStr()
end function

function trimTitle(title as string) as string
    if Len(title) <= 34
        return title
    end if

    return Left(title, 31) + "..."
end function

function normalizeBridgeHost(value as string) as string
    host = Trim(value)
    if host = ""
        return m.defaultBridgeHost
    end if

    if Instr(1, host, ":") = 0
        host = host + ":8090"
    end if

    return host
end function

function isInstantReplayKey(key as string) as boolean
    return key = "instantreplay" or key = "replay"
end function

function isRevKey(key as string) as boolean
    return key = "rev" or key = "reverse" or key = "rewind"
end function

function isFwdKey(key as string) as boolean
    return key = "fwd" or key = "forward" or key = "fastforward"
end function

function isPlusKey(key as string) as boolean
    return key = "volumeup" or key = "channelup"
end function

function isMinusKey(key as string) as boolean
    return key = "volumedown" or key = "channeldown"
end function

function isOptionsKey(key as string) as boolean
    return key = "info" or key = "options"
end function
