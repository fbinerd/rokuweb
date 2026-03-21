sub init()
    m.defaultBridgeHostBase = "superweb.local"
    m.defaultBridgePorts = [8090, 8091, 8092, 8093]
    m.defaultBridgeHost = m.defaultBridgeHostBase + ":" + m.defaultBridgePorts[0].ToStr()
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
    m.autoConnectAttempts = 0
    m.isAutoConnecting = true
    m.keyboardPurpose = ""
    deviceInfo = CreateObject("roDeviceInfo")
    m.deviceModel = deviceInfo.GetModel()
    m.firmwareVersion = deviceInfo.GetVersion()
    m.channelVersion = GetRokuChannelReleaseId()
    m.deviceId = "roku-" + m.deviceModel + "-" + m.firmwareVersion

    m.titleLabel = m.top.findNode("titleLabel")
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.versionLabel = m.top.findNode("versionLabel")
    m.fullscreenPosterA = m.top.findNode("fullscreenPosterA")
    m.fullscreenPosterB = m.top.findNode("fullscreenPosterB")
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.cursorMarker = m.top.findNode("cursorMarker")
    m.fullscreenVideo = m.top.findNode("fullscreenVideo")
    m.bridgeRequestTask = m.top.findNode("bridgeRequestTask")
    m.inputLogTask = m.top.findNode("inputLogTask")
    m.controlTask = m.top.findNode("controlTask")
    m.clickControlTask = m.top.findNode("clickControlTask")
    m.textControlTask = m.top.findNode("textControlTask")
    m.experimentalAvSessionTask = m.top.findNode("experimentalAvSessionTask")
    m.experimentalAvOfferTask = m.top.findNode("experimentalAvOfferTask")
    m.experimentalAvStateTask = m.top.findNode("experimentalAvStateTask")
    m.experimentalAvMediaTask = m.top.findNode("experimentalAvMediaTask")
    m.panelRefreshTimer = m.top.findNode("panelRefreshTimer")
    m.previewRefreshTimer = m.top.findNode("previewRefreshTimer")
    m.autoConnectTimer = m.top.findNode("autoConnectTimer")
    m.fullscreenStreamTimer = m.top.findNode("fullscreenStreamTimer")
    m.cursorMoveTimer = m.top.findNode("cursorMoveTimer")
    m.experimentalAvStateTimer = m.top.findNode("experimentalAvStateTimer")
    m.experimentalAvMediaProbeTimer = m.top.findNode("experimentalAvMediaProbeTimer")
    m.experimentalAvMode = false
    m.experimentalAvOfferPosted = false
    m.experimentalAvStateUrl = ""
    m.experimentalAvMediaUrl = ""
    m.experimentalAvLastAction = ""
    m.experimentalAvPlaybackStarted = false
    m.experimentalAvStateRequestInFlight = false
    m.experimentalAvMediaProbeInFlight = false

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
    m.cursorMoveTimer.observeField("fire", "onCursorMoveTimerFire")
    m.experimentalAvStateTimer.observeField("fire", "onExperimentalAvStateTimerFire")
    m.experimentalAvMediaProbeTimer.observeField("fire", "onExperimentalAvMediaProbeTimerFire")
    m.fullscreenPosterA.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    m.fullscreenPosterB.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    if m.fullscreenVideo <> invalid
        m.fullscreenVideo.observeField("state", "onFullscreenVideoStateChanged")
    end if
    m.clickControlTask.observeField("completedToken", "onClickControlTaskCompleted")
    m.textControlTask.observeField("completedToken", "onTextControlTaskCompleted")
    m.experimentalAvSessionTask.observeField("completedToken", "onExperimentalAvSessionTaskCompleted")
    m.experimentalAvOfferTask.observeField("completedToken", "onExperimentalAvOfferTaskCompleted")
    m.experimentalAvStateTask.observeField("completedToken", "onExperimentalAvStateTaskCompleted")
    m.experimentalAvMediaTask.observeField("completedToken", "onExperimentalAvMediaTaskCompleted")
    m.top.setFocus(true)

    m.titleLabel.text = GetRokuAppShortName()
    m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
    m.subtitleLabel.text = "Procurando automaticamente o super..."
    m.versionLabel.text = "Canal " + GetRokuChannelName() + " | " + GetRokuChannelReleaseId()
    hideGrid()
    beginAutoConnect()
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if m.isKeyboardOpen
        return false
    end if

    if m.isFullscreen
        if not press
            if key = m.heldDirectionKey
                stopHeldDirection()
                return true
            end if

            return false
        end if

        reportInputKey(key)

        if key = "back" or key = "Back"
            hideFullscreen()
            return true
        end if

        if key = "OK"
            sendClickCommand()
            return true
        end if

        if key = "up" or key = "down" or key = "left" or key = "right"
            startHeldDirection(key)
            return true
        end if

        if key = "Play"
            sendRemoteCommand("tab")
            scheduleFullscreenRefresh()
            return true
        end if

        return false
    end if

    if not press
        return false
    end if

    reportInputKey(key)

    if key = "up"
        moveSelection(-m.gridColumns)
        return true
    end if

    if key = "down"
        moveSelection(m.gridColumns)
        return true
    end if

    if key = "left"
        moveSelection(-1)
        return true
    end if

    if key = "right"
        moveSelection(1)
        return true
    end if

    if key = "OK"
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

    if key = "Play"
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
            initialUrl: getString(window.initialUrl, "")
            experimentalAvUrl: getString(window.experimentalAvUrl, "")
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

    m.pageStart = 0

    m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
    m.isAutoConnecting = false
    startPanelRefresh()
    refreshGrid()
    m.top.setFocus(true)
end sub

sub beginAutoConnect()
    m.autoConnectAttempts = 0
    m.isAutoConnecting = true
    m.bridgeHost = resolveAutoConnectBridgeHost()
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
        m.bridgeHost = resolveAutoConnectBridgeHost()
        loadWindows()
        return
    end if

    m.bridgeHost = resolveAutoConnectBridgeHost()
    m.statusLabel.text = "Tentando conectar em " + m.bridgeHost
    m.subtitleLabel.text = "Tentativa " + m.autoConnectAttempts.ToStr() + " falhou. Tentando novamente automaticamente..."
    m.autoConnectTimer.control = "stop"
    m.autoConnectTimer.control = "start"
end sub

sub onAutoConnectTimerFire()
    if m.isAutoConnecting
        m.bridgeHost = resolveAutoConnectBridgeHost()
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
    m.cursorX = 640
    m.cursorY = 360
    hideGrid()
    m.titleLabel.visible = false
    m.statusLabel.visible = false
    m.subtitleLabel.visible = false
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.activeFullscreenPoster.uri = appendCacheBust(entry.thumbnailUrl)
    m.activeFullscreenPoster.visible = true
    if m.fullscreenVideo <> invalid
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.content = invalid
        m.fullscreenVideo.visible = false
    end if
    m.bufferFullscreenPoster.visible = false
    m.bufferFullscreenPoster.uri = ""
    m.cursorMarker.visible = true
    m.isFullscreenRefreshInFlight = false
    m.experimentalAvMode = false
    m.experimentalAvOfferPosted = false
    m.experimentalAvStateUrl = ""
    m.experimentalAvLastAction = ""
    m.experimentalAvPlaybackStarted = false
    stopPanelRefresh()
    updateCursorMarker()
    maybeStartExperimentalAv(entry)
    startFullscreenStream()
    m.top.setFocus(true)
end sub

sub hideFullscreen()
    m.isFullscreen = false
    stopHeldDirection()
    stopFullscreenStream()
    m.fullscreenPosterA.visible = false
    m.fullscreenPosterB.visible = false
    if m.fullscreenVideo <> invalid
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.content = invalid
        m.fullscreenVideo.visible = false
    end if
    m.cursorMarker.visible = false
    m.titleLabel.visible = true
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    stopExperimentalAv()
    startPanelRefresh()
    refreshGrid()
    m.top.setFocus(true)
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
    refreshFullscreenPreview()
end sub

sub maybeStartExperimentalAv(entry as object)
    experimentalAvUrl = getString(entry.experimentalAvUrl, "")
    ? "[ExpAV] entry="; getString(entry.id, ""); " url="; experimentalAvUrl
    if experimentalAvUrl = ""
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Painel sem sessao experimental A/V"
        m.subtitleLabel.text = "Continuando no preview por imagem."
        return
    end if

    m.experimentalAvMode = true
    m.experimentalAvOfferPosted = false
    m.experimentalAvStateUrl = ""
    m.experimentalAvLastAction = "session"
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    m.statusLabel.text = "Sessao experimental A/V detectada"
    m.subtitleLabel.text = "Consultando sessao experimental..."
    ? "[ExpAV] session GET => "; experimentalAvUrl
    runExperimentalAvRequest(m.experimentalAvSessionTask, experimentalAvUrl, "GET", "")
end sub

sub stopExperimentalAv()
    m.experimentalAvMode = false
    m.experimentalAvOfferPosted = false
    m.experimentalAvStateUrl = ""
    m.experimentalAvMediaUrl = ""
    m.experimentalAvLastAction = ""
    m.experimentalAvPlaybackStarted = false
    m.experimentalAvStateRequestInFlight = false
    m.experimentalAvMediaProbeInFlight = false
    if m.experimentalAvStateTimer <> invalid
        m.experimentalAvStateTimer.control = "stop"
    end if
    if m.experimentalAvMediaProbeTimer <> invalid
        m.experimentalAvMediaProbeTimer.control = "stop"
    end if
end sub

sub runExperimentalAvRequest(task as object, url as string, method as string, body as string)
    if task = invalid or url = ""
        return
    end if

    ? "[ExpAV] request => "; method; " "; url
    task.control = "stop"
    task.bridgeUrl = url
    task.httpMethod = method
    task.requestBody = body
    task.responseCode = 0
    task.responseBody = ""
    task.errorMessage = ""
    task.control = "RUN"
end sub

sub onExperimentalAvSessionTaskCompleted()
    if not m.experimentalAvMode or m.experimentalAvSessionTask = invalid
        return
    end if

    responseBody = m.experimentalAvSessionTask.responseBody
    ? "[ExpAV] responseCode="; m.experimentalAvSessionTask.responseCode; " action=session"
    if responseBody = invalid or responseBody = ""
        ? "[ExpAV] empty response"
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Sessao experimental A/V sem resposta"
        m.subtitleLabel.text = "Continuando no preview atual."
        return
    end if

    json = ParseJson(responseBody)
    if json = invalid
        ? "[ExpAV] invalid json"
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Resposta invalida da sessao experimental"
        m.subtitleLabel.text = "Continuando no preview atual."
        return
    end if

    offerUrl = getString(json.offerUrl, "")
    stateUrl = getString(json.stateUrl, "")
    if offerUrl = "" or stateUrl = ""
        ? "[ExpAV] missing offer/state url"
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Sessao experimental incompleta"
        m.subtitleLabel.text = "offerUrl/stateUrl ausentes."
        return
    end if

    m.experimentalAvStateUrl = stateUrl
    m.experimentalAvLastAction = "offer"
    ? "[ExpAV] session ok => offer="; offerUrl; " state="; stateUrl
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    m.statusLabel.text = "Sessao experimental encontrada"
    m.subtitleLabel.text = "Enviando offer para " + trimTitle(offerUrl)
    offerBody = "{""type"":""offer"",""sdp"":""roku-placeholder-offer"",""source"":""roku-scenegraph""}"
    runExperimentalAvRequest(m.experimentalAvOfferTask, offerUrl, "POST", offerBody)
end sub

sub onExperimentalAvOfferTaskCompleted()
    if not m.experimentalAvMode or m.experimentalAvOfferTask = invalid
        return
    end if

    responseBody = m.experimentalAvOfferTask.responseBody
    ? "[ExpAV] responseCode="; m.experimentalAvOfferTask.responseCode; " action=offer"
    if responseBody = invalid or responseBody = ""
        ? "[ExpAV] empty offer response"
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Offer experimental sem resposta"
        m.subtitleLabel.text = "Continuando no preview atual."
        return
    end if

    json = ParseJson(responseBody)
    if json <> invalid
        answerType = getString(json.answerType, "")
        if answerType <> ""
            ? "[ExpAV] answer => "; answerType
        end if
        mediaUrl = getString(json.mediaUrl, "")
        if mediaUrl <> ""
            m.experimentalAvMediaUrl = mediaUrl
            ? "[ExpAV] media => "; mediaUrl
        end if
        transportStatus = getString(json.transportStatus, "")
        if transportStatus <> ""
            ? "[ExpAV] transport => "; transportStatus
        end if
    end if

    m.experimentalAvOfferPosted = true
    m.experimentalAvLastAction = "state"
    ? "[ExpAV] offer accepted"
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    m.statusLabel.text = "Offer experimental entregue"
    m.subtitleLabel.text = "Acompanhando state da sessao..."
    scheduleExperimentalAvStatePoll()
    scheduleExperimentalAvMediaProbe()
end sub

sub onExperimentalAvStateTaskCompleted()
    if not m.experimentalAvMode or m.experimentalAvStateTask = invalid
        return
    end if

    m.experimentalAvStateRequestInFlight = false
    responseBody = m.experimentalAvStateTask.responseBody
    ? "[ExpAV] responseCode="; m.experimentalAvStateTask.responseCode; " action=state"
    if responseBody = invalid or responseBody = ""
        ? "[ExpAV] empty state response"
        return
    end if

    json = ParseJson(responseBody)
    if json = invalid
        ? "[ExpAV] invalid state json"
        return
    end if

    statusText = getString(json.status, "desconhecido")
    offerCount = 0
    if json.offerCount <> invalid
        offerCount = json.offerCount
    end if
    mediaReady = false
    if json.mediaReady <> invalid
        mediaReady = json.mediaReady
    end if
    transportImplemented = false
    if json.mediaTransportImplemented <> invalid
        transportImplemented = json.mediaTransportImplemented
    end if
    transportStatus = getString(json.transportStatus, "")
    mediaUrl = getString(json.mediaUrl, "")
    if mediaUrl <> ""
        m.experimentalAvMediaUrl = mediaUrl
    end if

    ? "[ExpAV] state => "; statusText; " offers="; offerCount; " mediaReady="; mediaReady; " transport="; transportImplemented; " transportStatus="; transportStatus; " mediaUrl="; mediaUrl
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Sessao experimental: " + statusText
        if mediaReady and m.experimentalAvMediaUrl <> "" and not m.experimentalAvPlaybackStarted
            ? "[ExpAV] state ready => play media"
            startExperimentalMediaPlayback(m.experimentalAvMediaUrl)
            m.experimentalAvPlaybackStarted = true
        end if
        if transportImplemented
            m.subtitleLabel.text = "Offers recebidas pelo super: " + offerCount.ToStr()
        else
            if transportStatus <> ""
                m.subtitleLabel.text = "Sinalizacao OK; transporte=" + transportStatus
            else
                m.subtitleLabel.text = "Sinalizacao OK; transporte A/V experimental ainda nao conectado."
            end if
        end if
        if not m.experimentalAvPlaybackStarted and m.experimentalAvMediaUrl <> ""
            scheduleExperimentalAvMediaProbe()
        end if
        scheduleExperimentalAvStatePoll()
end sub

sub startExperimentalMediaPlayback(mediaUrl as string)
    if m.fullscreenVideo = invalid or mediaUrl = ""
        return
    end if

    ? "[ExpAV] play media => "; mediaUrl
    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(mediaUrl)
    content.streamFormat = "mp4"
    content.live = false
    content.title = "Experimental AV"
    m.fullscreenVideo.content = content
    m.fullscreenVideo.control = "stop"
    m.fullscreenVideo.visible = true
    m.fullscreenVideo.control = "play"
    m.activeFullscreenPoster.visible = false
    m.bufferFullscreenPoster.visible = false
end sub

sub onFullscreenVideoStateChanged()
    if m.fullscreenVideo = invalid or not m.isFullscreen
        return
    end if

    state = LCase(getString(m.fullscreenVideo.state, ""))
    if state = ""
        return
    end if

    ? "[ExpAV] video state => "; state
    if state = "finished"
        m.experimentalAvPlaybackStarted = false
        m.fullscreenVideo.visible = false
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.content = invalid
        m.activeFullscreenPoster.visible = true
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Clip experimental concluido"
        m.subtitleLabel.text = "Aguardando nova sessao."
        return
    end if

    if state = "error" or state = "stopped"
        m.experimentalAvPlaybackStarted = false
        m.fullscreenVideo.visible = false
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.content = invalid
        m.activeFullscreenPoster.visible = true
        m.statusLabel.visible = true
        m.subtitleLabel.visible = true
        m.statusLabel.text = "Falha temporaria na midia experimental"
        m.subtitleLabel.text = "Tentando novamente..."
        scheduleExperimentalAvMediaProbe()
    end if
end sub

sub scheduleExperimentalAvStatePoll()
    if not m.experimentalAvMode or m.experimentalAvStateUrl = "" or m.experimentalAvStateTimer = invalid
        return
    end if

    m.experimentalAvStateTimer.control = "stop"
    m.experimentalAvStateTimer.control = "start"
end sub

sub onExperimentalAvStateTimerFire()
    if not m.experimentalAvMode or m.experimentalAvStateUrl = ""
        return
    end if

    if m.experimentalAvStateRequestInFlight
        scheduleExperimentalAvStatePoll()
        return
    end if

    m.experimentalAvStateRequestInFlight = true
    m.experimentalAvLastAction = "state"
    runExperimentalAvRequest(m.experimentalAvStateTask, m.experimentalAvStateUrl, "GET", "")
end sub

sub scheduleExperimentalAvMediaProbe()
    if not m.experimentalAvMode or m.experimentalAvMediaUrl = "" or m.experimentalAvMediaProbeTimer = invalid
        return
    end if

    m.experimentalAvMediaProbeTimer.control = "stop"
    m.experimentalAvMediaProbeTimer.control = "start"
end sub

sub onExperimentalAvMediaProbeTimerFire()
    if not m.experimentalAvMode or m.experimentalAvMediaUrl = ""
        return
    end if

    if m.experimentalAvPlaybackStarted
        return
    end if

    if m.experimentalAvMediaProbeInFlight
        scheduleExperimentalAvMediaProbe()
        return
    end if

    m.experimentalAvMediaProbeInFlight = true
    probeUrl = m.experimentalAvMediaUrl
    if Instr(1, probeUrl, "?") > 0
        probeUrl = probeUrl + "&probe=1"
    else
        probeUrl = probeUrl + "?probe=1"
    end if
    runExperimentalAvRequest(m.experimentalAvMediaTask, probeUrl, "GET", "")
end sub

sub onExperimentalAvMediaTaskCompleted()
    if not m.experimentalAvMode or m.experimentalAvMediaTask = invalid
        return
    end if

    m.experimentalAvMediaProbeInFlight = false
    code = m.experimentalAvMediaTask.responseCode
    responseBody = m.experimentalAvMediaTask.responseBody
    ready = false
    if responseBody <> invalid and responseBody <> ""
        probeJson = ParseJson(responseBody)
        if probeJson <> invalid and probeJson.ready <> invalid
            ready = probeJson.ready = true
        end if
    end if
    ? "[ExpAV] media probe => "; code; " ready="; ready

    if m.experimentalAvPlaybackStarted
        return
    end if

    if ready and m.experimentalAvMediaUrl <> ""
        startExperimentalMediaPlayback(m.experimentalAvMediaUrl)
        m.experimentalAvPlaybackStarted = true
        return
    end if

    scheduleExperimentalAvMediaProbe()
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
        openKeyboardDialog(getString(result.value, ""), result.multiline = true)
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

function resolveAutoConnectBridgeHost() as string
    if m.defaultBridgePorts = invalid or m.defaultBridgePorts.Count() = 0
        return m.defaultBridgeHost
    end if

    portIndex = m.autoConnectAttempts mod m.defaultBridgePorts.Count()
    return m.defaultBridgeHostBase + ":" + m.defaultBridgePorts[portIndex].ToStr()
end function
