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
    m.heldRemoteCommand = ""
    m.heldRemoteKey = ""
    m.cursorX = 640
    m.cursorY = 360
    m.lockedFullscreen = false
    m.lockedWindowId = ""
    m.backHoldTimespan = invalid
    m.backLongPressThresholdMs = 650
    m.scrollModeEnabled = false
    m.autoConnectAttempts = 0
    m.isAutoConnecting = true
    m.keyboardPurpose = ""
    deviceInfo = CreateObject("roDeviceInfo")
    m.deviceModel = deviceInfo.GetModel()
    m.firmwareVersion = deviceInfo.GetVersion()
    m.channelVersion = GetRokuChannelReleaseId()
    m.deviceId = "roku-" + m.deviceModel + "-" + m.firmwareVersion
    m.displayReadySent = false
    m.audioSessionId = ""
    m.audioMode = ""
    m.audioChunkUrl = ""
    m.audioPlayer = invalid
    m.audioUsesHls = false
    m.videoUsesStream = false
    m.videoStreamUrl = ""
    m.fullscreenStreamingMode = "Interacao"
    m.fullscreenWindowId = ""
    m.fullscreenAssignedStreamUrl = ""
    m.modeSwitchState = "idle"
    m.pendingModeSwitchWindowId = ""
    m.pendingModeSwitchCurrentMode = ""
    m.pendingModeSwitchTargetMode = ""
    m.pendingModeSwitchAckSent = false
    m.pendingAutoFullscreenWindowId = ""
    m.pendingAutoFullscreenStreamUrl = ""
    m.pendingAutoFullscreenMode = ""
    m.pendingAutoFullscreenSeenCount = 0
    m.lastInteractionOkTimespan = invalid
    m.interactionOkDebounceMs = 1200
    m.fullscreenOkActive = false
    m.pendingInteractionAutoPlayWindowId = ""
    m.fullscreenPlayRequestCount = 0
    m.interactionOverlayControlsVisible = false
    m.interactionOverlayControlsFullscreen = false
    m.interactionOverlayControlsHideDelayMs = 5000
    m.interactionOverlayControlsLastActivity = invalid
    m.interactionOverlayBaseRect = invalid
    m.interactionOverlayCurrentRect = invalid
    m.interactionOverlayIntrinsicRect = invalid
    m.interactionOverlayPlayButtonRect = invalid
    m.interactionOverlayFullscreenButtonRect = invalid
    m.interactionOverlayProgressTrackRect = invalid
    m.interactionOverlayQualityButtonRect = invalid
    m.lastInteractionOverlayState = ""
    m.lastPointerMoveDispatch = invalid
    m.pointerMoveDispatchIntervalMs = 120
    m.heldDirectionRepeatCount = 0
    m.interactionOverlayQualityOptions = []
    m.interactionOverlaySelectedQualityIndex = 0
    m.interactionOverlayPendingSeekPosition = invalid
    m.interactionOverlaySourceUrl = ""
    m.interactionOverlayQualityMenuVisible = false
    m.interactionOverlayQualityMenuRects = []
    m.interactionOverlayQualityMenuNodeIndices = []
    m.interactionOverlayQualityMenuSelectedIndex = 0
    m.interactionOverlayAutoMode = true
    m.interactionOverlayAutoDegradeLevel = 0
    m.interactionOverlayAutoBufferingCount = 0
    m.displayWidth = 1280
    m.displayHeight = 720
    m.lastHeldRemoteDispatch = invalid
    m.heldRemoteDispatchIntervalMs = 140
    m.interactionOverlaySeekStepSeconds = 6
    m.fullscreenVideoPendingRestart = false
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
    m.lastVideoDiagEvent = ""
    m.lastVideoDiagHeartbeat = invalid
    m.fullscreenPosterWindowId = ""
    m.fullscreenPosterSourceUrl = ""
    m.lastBridgeCompletedToken = ""
    m.bridgeDiagnosticsCount = 0
    m.showFullscreenDiagnosticsCount = 0

    m.titleLabel = m.top.findNode("titleLabel")
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.versionLabel = m.top.findNode("versionLabel")
    m.background = m.top.findNode("background")
    m.fullscreenToastGroup = m.top.findNode("fullscreenToastGroup")
    m.fullscreenToastLabel = m.top.findNode("fullscreenToastLabel")
    m.fullscreenPosterA = m.top.findNode("fullscreenPosterA")
    m.fullscreenPosterB = m.top.findNode("fullscreenPosterB")
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.cursorMarker = m.top.findNode("cursorMarker")
    m.bridgeRequestTask = m.top.findNode("bridgeRequestTask")
    m.inputLogTask = m.top.findNode("inputLogTask")
    m.modeSwitchNotifyTask = m.top.findNode("modeSwitchNotifyTask")
    m.controlTask = m.top.findNode("controlTask")
    m.clickControlTask = m.top.findNode("clickControlTask")
    m.textControlTask = m.top.findNode("textControlTask")
    m.panelRefreshTimer = m.top.findNode("panelRefreshTimer")
    m.previewRefreshTimer = m.top.findNode("previewRefreshTimer")
    m.editableActivationTimer = m.top.findNode("editableActivationTimer")
    m.autoConnectTimer = m.top.findNode("autoConnectTimer")
    m.fullscreenStreamTimer = m.top.findNode("fullscreenStreamTimer")
    m.fullscreenVideoWatchTimer = m.top.findNode("fullscreenVideoWatchTimer")
    m.scrollModeToastTimer = m.top.findNode("scrollModeToastTimer")
    m.cursorMoveTimer = m.top.findNode("cursorMoveTimer")
    m.interactionOverlayControlsTimer = m.top.findNode("interactionOverlayControlsTimer")
    m.audioRetryTimer = m.top.findNode("audioRetryTimer")
    m.audioFallbackTimer = m.top.findNode("audioFallbackTimer")
    m.audioHlsRestartTimer = m.top.findNode("audioHlsRestartTimer")
    m.panelAudioNode = m.top.findNode("panelAudioNode")
    m.panelAudioVideo = m.top.findNode("panelAudioVideo")
    m.fullscreenVideo = m.top.findNode("fullscreenInteractionVideo")
    m.fullscreenInteractionOverlayVideo = m.top.findNode("fullscreenInteractionOverlayVideo")
    m.interactionOverlayControlsGroup = m.top.findNode("interactionOverlayControlsGroup")
    m.interactionOverlayViewport = m.top.findNode("fullscreenInteractionOverlayViewport")
    m.interactionOverlayControlsBackground = m.top.findNode("interactionOverlayControlsBackground")
    m.interactionOverlayProgressTrack = m.top.findNode("interactionOverlayProgressTrack")
    m.interactionOverlayProgressFill = m.top.findNode("interactionOverlayProgressFill")
    m.interactionOverlayLeftCapsule = m.top.findNode("interactionOverlayLeftCapsule")
    m.interactionOverlayTitleCapsule = m.top.findNode("interactionOverlayTitleCapsule")
    m.interactionOverlayRightCapsule = m.top.findNode("interactionOverlayRightCapsule")
    m.interactionOverlayTimeLabel = m.top.findNode("interactionOverlayTimeLabel")
    m.interactionOverlayPlayButton = m.top.findNode("interactionOverlayPlayButton")
    m.interactionOverlayPlayLabel = m.top.findNode("interactionOverlayPlayLabel")
    m.interactionOverlayTitleLabel = m.top.findNode("interactionOverlayTitleLabel")
    m.interactionOverlayQualityButton = m.top.findNode("interactionOverlayQualityButton")
    m.interactionOverlayQualityLabel = m.top.findNode("interactionOverlayQualityLabel")
    m.interactionOverlayQualityMenuGroup = m.top.findNode("interactionOverlayQualityMenuGroup")
    m.interactionOverlayQualityMenuBackground = m.top.findNode("interactionOverlayQualityMenuBackground")
    m.interactionOverlayQualityMenuItemBackgrounds = [
        m.top.findNode("interactionOverlayQualityMenuItemBg0")
        m.top.findNode("interactionOverlayQualityMenuItemBg1")
        m.top.findNode("interactionOverlayQualityMenuItemBg2")
        m.top.findNode("interactionOverlayQualityMenuItemBg3")
        m.top.findNode("interactionOverlayQualityMenuItemBg4")
        m.top.findNode("interactionOverlayQualityMenuItemBg5")
        m.top.findNode("interactionOverlayQualityMenuItemBg6")
        m.top.findNode("interactionOverlayQualityMenuItemBg7")
    ]
    m.interactionOverlayQualityMenuItemLabels = [
        m.top.findNode("interactionOverlayQualityMenuItemLabel0")
        m.top.findNode("interactionOverlayQualityMenuItemLabel1")
        m.top.findNode("interactionOverlayQualityMenuItemLabel2")
        m.top.findNode("interactionOverlayQualityMenuItemLabel3")
        m.top.findNode("interactionOverlayQualityMenuItemLabel4")
        m.top.findNode("interactionOverlayQualityMenuItemLabel5")
        m.top.findNode("interactionOverlayQualityMenuItemLabel6")
        m.top.findNode("interactionOverlayQualityMenuItemLabel7")
    ]
    m.interactionOverlayFullscreenButton = m.top.findNode("interactionOverlayFullscreenButton")
    m.interactionOverlayFullscreenLabel = m.top.findNode("interactionOverlayFullscreenLabel")
    m.fullscreenVideoMode = m.top.findNode("fullscreenVideoModeVideo")
    m.fullscreenVideoStage = m.top.findNode("fullscreenVideoStage")
    m.stagingVideoTargetMode = ""
    m.stagingVideoWindowId = ""
    m.stagingVideoStreamUrl = ""
    m.interactionOverlayAssignedStreamUrl = ""

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
    m.panelPosterWindowIds = ["", "", "", "", "", ""]

    m.panelLabels = [
        m.top.findNode("panelLabel0")
        m.top.findNode("panelLabel1")
        m.top.findNode("panelLabel2")
        m.top.findNode("panelLabel3")
        m.top.findNode("panelLabel4")
        m.top.findNode("panelLabel5")
    ]

    m.bridgeRequestTask.observeField("responseCode", "onBridgeResponseCodeChanged")
    m.bridgeRequestTask.observeField("completedToken", "onBridgeRequestCompleted")
    m.panelRefreshTimer.observeField("fire", "onPanelRefreshTimerFire")
    m.previewRefreshTimer.observeField("fire", "onPreviewRefreshTimerFire")
    m.autoConnectTimer.observeField("fire", "onAutoConnectTimerFire")
    m.fullscreenStreamTimer.observeField("fire", "onFullscreenStreamTimerFire")
    m.fullscreenVideoWatchTimer.observeField("fire", "onFullscreenVideoWatchTimerFire")
    m.scrollModeToastTimer.observeField("fire", "onScrollModeToastTimerFire")
    m.cursorMoveTimer.observeField("fire", "onCursorMoveTimerFire")
    m.interactionOverlayControlsTimer.observeField("fire", "onInteractionOverlayControlsTimerFire")
    m.audioRetryTimer.observeField("fire", "onAudioRetryTimerFire")
    m.audioFallbackTimer.observeField("fire", "onAudioFallbackTimerFire")
    m.audioHlsRestartTimer.observeField("fire", "onAudioHlsRestartTimerFire")
    m.fullscreenPosterA.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    m.fullscreenPosterB.observeField("loadStatus", "onBufferPosterLoadStatusChanged")
    m.controlTask.observeField("completedToken", "onControlTaskCompleted")
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
        m.fullscreenVideo.observeField("state", "onFullscreenInteractionVideoStateChanged")
    end if
    if m.fullscreenInteractionOverlayVideo <> invalid
        m.fullscreenInteractionOverlayVideo.observeField("state", "onInteractionOverlayVideoStateChanged")
    end if
    if m.fullscreenVideoMode <> invalid
        m.fullscreenVideoMode.observeField("state", "onFullscreenVideoModeStateChanged")
    end if
    if m.fullscreenVideoStage <> invalid
        m.fullscreenVideoStage.observeField("state", "onFullscreenVideoStageStateChanged")
    end if
    m.top.setFocus(true)
    m.pendingEditableActivation = false
    m.pendingEditableValue = ""
    m.pendingEditableMultiline = false
    m.blueModifierActive = false

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
            if normalizedKey = "ok"
                m.fullscreenOkActive = false
                return true
            end if

            if isBlueKey(normalizedKey)
                return true
            end if

            if normalizedKey = "back"
                heldMs = 0
                if m.backHoldTimespan <> invalid
                    heldMs = m.backHoldTimespan.TotalMilliseconds()
                end if
                m.backHoldTimespan = invalid

                if heldMs < m.backLongPressThresholdMs and isInteractionOverlayActive() and m.interactionOverlayControlsFullscreen
                    ? "[OVERLAY] back => minimize"
                    toggleInteractionOverlayFullscreen()
                    showInteractionOverlayControls("back")
                    return true
                end if

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

            if normalizedKey = m.heldRemoteKey
                stopHeldRemoteCommand()
                return true
            end if

            return false
        end if

        reportInputKey(normalizedKey)

        if normalizedKey = "back"
            if m.interactionOverlayQualityMenuVisible
                hideInteractionOverlayQualityMenu()
                showInteractionOverlayControls("back-close-quality-menu")
                return true
            end if
            m.backHoldTimespan = CreateObject("roTimespan")
            m.backHoldTimespan.Mark()
            return true
        end if

        if isBlueKey(normalizedKey)
            m.scrollModeEnabled = not m.scrollModeEnabled
            ? "[SCROLL] mode => "; m.scrollModeEnabled; " key="; normalizedKey
            if m.scrollModeEnabled
                showScrollModeToast("Modo de rolagem ativado")
            else
                showScrollModeToast("Modo de rolagem desativado")
            end if
            return true
        end if

        if normalizedKey = "ok"
            if m.fullscreenOkActive
                return true
            end if
            m.fullscreenOkActive = true

            if m.pendingEditableActivation
                openKeyboardDialog(m.pendingEditableValue, m.pendingEditableMultiline)
                clearPendingEditableActivation()
                return true
            end if
            if normalizeStreamingMode(m.fullscreenStreamingMode) = "Interacao"
                if m.interactionOverlayQualityMenuVisible
                    hoveredMenuIndex = getInteractionOverlayHoveredQualityMenuIndex()
                    if hoveredMenuIndex >= 0
                        selectInteractionOverlayQualityMenuIndex(hoveredMenuIndex, "ok-hover")
                    else
                        selectInteractionOverlayQualityMenuIndex(m.interactionOverlayQualityMenuSelectedIndex, "ok")
                    end if
                    return true
                end if

                overlayHitTarget = getInteractionOverlayControlHitTarget()
                if overlayHitTarget <> ""
                    handleInteractionOverlayControlHit(overlayHitTarget)
                    return true
                end if

                if shouldCaptureInteractionOverlayPointerInput()
                    showInteractionOverlayControls("ok")
                    toggleInteractionOverlayPlayback()
                    return true
                end if

                if m.lastInteractionOkTimespan = invalid
                    m.lastInteractionOkTimespan = CreateObject("roTimespan")
                    m.lastInteractionOkTimespan.Mark()
                else if m.lastInteractionOkTimespan.TotalMilliseconds() < m.interactionOkDebounceMs
                    return true
                else
                    m.lastInteractionOkTimespan.Mark()
                end if

                if m.selectedIndex >= 0 and m.selectedIndex < m.windowEntries.Count()
                    currentEntry = m.windowEntries[m.selectedIndex]
                    initialUrl = LCase(getString(currentEntry.initialUrl, ""))
                    if Instr(1, initialUrl, "youtube.com") > 0 or Instr(1, initialUrl, "youtu.be") > 0 or Instr(1, initialUrl, "youtube-nocookie.com") > 0
                        sendClickCommand()
                        scheduleFullscreenRefresh()
                        return true
                    end if
                end if
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
            if m.scrollModeEnabled
                startHeldRemoteCommand(normalizedKey, "scroll-down")
            else
                sendRemoteCommand("history-back")
            end if
            scheduleFullscreenRefresh()
            return true
        end if

        if isPlusKey(normalizedKey)
            clearPendingEditableActivation()
            if m.scrollModeEnabled
                startHeldRemoteCommand(normalizedKey, "scroll-up")
            else
                sendRemoteCommand("history-forward")
            end if
            scheduleFullscreenRefresh()
            return true
        end if

        if isOptionsKey(normalizedKey)
            clearPendingEditableActivation()
            if isInteractionOverlayActive()
                toggleInteractionOverlayQualityMenu("options")
                return true
            end if
            sendRemoteCommand("enter")
            scheduleFullscreenRefresh()
            return true
        end if

        if isRevKey(normalizedKey)
            clearPendingEditableActivation()
            if isInteractionOverlayActive()
                handleInteractionOverlayTransportShortcut("backward")
                startHeldRemoteCommand(normalizedKey, "overlay-seek-backward")
                return true
            end if
            startHeldRemoteCommand(normalizedKey, "media-seek-backward")
            scheduleFullscreenRefresh()
            return true
        end if

        if isFwdKey(normalizedKey)
            clearPendingEditableActivation()
            if isInteractionOverlayActive()
                handleInteractionOverlayTransportShortcut("forward")
                startHeldRemoteCommand(normalizedKey, "overlay-seek-forward")
                return true
            end if
            startHeldRemoteCommand(normalizedKey, "media-seek-forward")
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
            if handleInteractionOverlayTransportShortcut("toggle")
                return true
            end if
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

sub reportVideoDiag(eventName as string, extra = "" as string)
    if not m.isFullscreen
        return
    end if

    streamUrl = getString(m.videoStreamUrl, "")
    rv = ""
    queryIndex = Instr(1, streamUrl, "rv=")
    if queryIndex > 0
        rv = Mid(streamUrl, queryIndex + 3)
        ampIndex = Instr(1, rv, "&")
        if ampIndex > 0
            rv = Left(rv, ampIndex - 1)
        end if
    end if

    payload = "diag:" + eventName + ":mode=" + normalizeStreamingMode(m.fullscreenStreamingMode)
    if rv <> ""
        payload = payload + ":rv=" + rv
    end if
    if extra <> ""
        payload = payload + ":" + extra
    end if

    if payload = m.lastVideoDiagEvent
        return
    end if

    m.lastVideoDiagEvent = payload
    reportInputKey(payload)
end sub

sub reportVideoDiagHeartbeat()
    if not m.isFullscreen or normalizeStreamingMode(m.fullscreenStreamingMode) <> "Video"
        return
    end if

    if m.lastVideoDiagHeartbeat = invalid
        m.lastVideoDiagHeartbeat = CreateObject("roTimespan")
        m.lastVideoDiagHeartbeat.Mark()
    else if m.lastVideoDiagHeartbeat.TotalMilliseconds() < 5000
        return
    else
        m.lastVideoDiagHeartbeat.Mark()
    end if

    if m.fullscreenVideoMode = invalid
        reportVideoDiag("video-heartbeat", "state=invalid-node")
        return
    end if

    currentState = LCase(getString(m.fullscreenVideoMode.state, ""))
    currentUrl = getString(m.videoStreamUrl, "")
    positionValue = m.fullscreenVideoMode.position
    if positionValue = invalid
        positionValue = -1
    end if

    extra = "state=" + currentState + ":pos=" + positionValue.ToStr()
    if currentUrl = ""
        extra = extra + ":url=empty"
    else
        extra = extra + ":url=set"
    end if

    reportVideoDiag("video-heartbeat", extra)
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

sub onBridgeRequestCompleted()
    token = getString(m.bridgeRequestTask.completedToken, "")
    if token = "" or token = m.lastBridgeCompletedToken
        return
    end if

    m.lastBridgeCompletedToken = token
    if m.bridgeRequestTask.responseCode < 0
        return
    end if

    applyBridgeResponse()
end sub

sub applyBridgeResponse()
    responseBody = m.bridgeRequestTask.responseBody
    responseCode = m.bridgeRequestTask.responseCode
    previousFullscreenWindowId = ""
    if m.isFullscreen and m.windowEntries.Count() > 0 and m.selectedIndex >= 0 and m.selectedIndex < m.windowEntries.Count()
        previousFullscreenWindowId = getString(m.windowEntries[m.selectedIndex].id, "")
    end if

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

    if json.displays <> invalid
        for each display in json.displays
            if getString(display.deviceId, "") = m.deviceId
                detectedWidth = Int(getNumber(display.screenWidth, 0))
                detectedHeight = Int(getNumber(display.screenHeight, 0))
                if detectedWidth > 0 and detectedHeight > 0
                    m.displayWidth = detectedWidth
                    m.displayHeight = detectedHeight
                    ? "[OVERLAY] display => "; m.displayWidth; "x"; m.displayHeight
                end if
                exit for
            end if
        end for
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
            streamingMode: resolveWindowEntryStreamingMode(window)
            requestedStreamingMode: normalizeOptionalStreamingMode(getString(window.requestedStreamingMode, ""))
            modeSwitchPending: getBool(window.modeSwitchPending, false)
            autoOpenFullscreen: getBool(window.autoOpenFullscreen, false)
            directVideoOverlayEnabled: getBool(window.directVideoOverlayEnabled, false)
            directVideoSourceUrl: getString(window.directVideoSourceUrl, "")
            directVideoStreamUrl: getString(window.directVideoStreamUrl, "")
            directVideoStreamFormat: getString(window.directVideoStreamFormat, "")
            directVideoNormalizedLeft: getNumber(window.directVideoNormalizedLeft, 0.0)
            directVideoNormalizedTop: getNumber(window.directVideoNormalizedTop, 0.0)
            directVideoNormalizedWidth: getNumber(window.directVideoNormalizedWidth, 0.0)
            directVideoNormalizedHeight: getNumber(window.directVideoNormalizedHeight, 0.0)
            directVideoQualityLabel: getString(window.directVideoQualityLabel, "")
            directVideoQualityOptions: getObjectArray(window.directVideoQualityOptions)
        })
    end for

    for each entry in m.windowEntries
        ? "[MODE] bridge entry => id="; getString(entry.id, ""); " modo="; getString(entry.streamingMode, ""); " pendente="; getBool(entry.modeSwitchPending, false); " alvo="; getString(entry.requestedStreamingMode, ""); " autoOpen="; getBool(entry.autoOpenFullscreen, false); " stream="; getString(entry.streamUrl, "")
    end for
    m.bridgeDiagnosticsCount = m.bridgeDiagnosticsCount + 1
    if m.bridgeDiagnosticsCount <= 3
        ? "[BOOT] bridge payload #"; m.bridgeDiagnosticsCount; " windows="; m.windowEntries.Count(); " autoIndex="; autoFullscreenIndex
    end if

    m.isAutoConnecting = false

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

    hasInteractionWindow = false
    for each windowEntry in m.windowEntries
        if normalizeStreamingMode(getString(windowEntry.streamingMode, "Interacao")) = "Interacao"
            hasInteractionWindow = true
            exit for
        end if
    end for

    if not m.displayReadySent
        notifyDisplayReady()
        m.displayReadySent = true
        if not hasInteractionWindow
            m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
            m.subtitleLabel.text = "TV pronta. Solicitando stream na proxima atualizacao..."
            startPanelRefresh()
            if m.isFullscreen
                hideGrid()
            else
                refreshGrid()
            end if
            return
        end if
    end if

    startPanelRefresh()
    if m.isFullscreen
        hideGrid()
    else
        refreshGrid()
    end if
    if not m.isFullscreen and autoFullscreenIndex >= 0
        autoEntry = m.windowEntries[autoFullscreenIndex]
        autoWindowId = getString(autoEntry.id, "")
        autoStreamUrl = getString(autoEntry.streamUrl, "")
        autoThumbnailUrl = getString(autoEntry.thumbnailUrl, "")
        autoAudioUrl = getString(autoEntry.audioStreamUrl, "")
        autoStreamingMode = normalizeStreamingMode(getString(autoEntry.streamingMode, "Interacao"))
        autoStabilityToken = autoStreamUrl

        if autoStreamingMode = "Interacao" and autoStabilityToken = ""
            if autoThumbnailUrl <> ""
                autoStabilityToken = autoThumbnailUrl
            else if autoAudioUrl <> ""
                autoStabilityToken = autoAudioUrl
            else
                autoStabilityToken = autoWindowId
            end if
        end if

        if autoWindowId <> "" and autoStabilityToken <> ""
            if m.pendingAutoFullscreenWindowId = autoWindowId and m.pendingAutoFullscreenStreamUrl = autoStabilityToken and m.pendingAutoFullscreenMode = autoStreamingMode
                m.pendingAutoFullscreenSeenCount = m.pendingAutoFullscreenSeenCount + 1
            else
                m.pendingAutoFullscreenWindowId = autoWindowId
                m.pendingAutoFullscreenStreamUrl = autoStabilityToken
                m.pendingAutoFullscreenMode = autoStreamingMode
                m.pendingAutoFullscreenSeenCount = 1
            end if

            if m.pendingAutoFullscreenSeenCount >= 2
                ? "[HLS] auto fullscreen confirmado => "; autoWindowId; " modo="; autoStreamingMode; " seen="; m.pendingAutoFullscreenSeenCount
                m.selectedIndex = autoFullscreenIndex
                showFullscreen()
                return
            end if

            ? "[HLS] auto fullscreen aguardando estabilidade => "; autoWindowId; " modo="; autoStreamingMode; " seen="; m.pendingAutoFullscreenSeenCount
        end if
    else if not m.isFullscreen
        m.pendingAutoFullscreenWindowId = ""
        m.pendingAutoFullscreenStreamUrl = ""
        m.pendingAutoFullscreenMode = ""
        m.pendingAutoFullscreenSeenCount = 0
    end if

    if m.isFullscreen
        if autoFullscreenIndex >= 0
            nextFullscreenWindowId = getString(m.windowEntries[autoFullscreenIndex].id, "")
            autoStreamingMode = normalizeStreamingMode(getString(m.windowEntries[autoFullscreenIndex].streamingMode, "Interacao"))
            if autoStreamingMode = "Interacao" and nextFullscreenWindowId <> "" and nextFullscreenWindowId <> previousFullscreenWindowId
                m.selectedIndex = autoFullscreenIndex
                showFullscreen()
                return
            end if

            if autoStreamingMode = "Video" and nextFullscreenWindowId <> "" and nextFullscreenWindowId <> previousFullscreenWindowId and nextFullscreenWindowId <> m.fullscreenWindowId
                m.selectedIndex = autoFullscreenIndex
                showFullscreen()
                return
            end if
        end if

        if previousFullscreenWindowId <> ""
            syncFullscreenStreamState(previousFullscreenWindowId)
        end if
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
            m.panelPosterWindowIds[slot] = ""
        else
            entry = m.windowEntries[absoluteIndex]
            group.visible = true
            entryId = getString(entry.id, "")
            if m.panelPosterWindowIds[slot] <> entryId
                poster.uri = appendCacheBust(entry.thumbnailUrl)
                m.panelPosterWindowIds[slot] = entryId
            end if
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
    for i = 0 to m.panelGroups.Count() - 1
        m.panelGroups[i].visible = false
        m.panelPosterWindowIds[i] = ""
    end for
end sub

sub notifyDisplayReady()
    if m.bridgeHost = invalid or m.bridgeHost = "" or m.deviceId = invalid or m.deviceId = ""
        return
    end if

    readyUrl = "http://" + m.bridgeHost + "/api/display-ready?deviceId=" + m.deviceId
    transfer = CreateObject("roUrlTransfer")
    transfer.SetUrl(readyUrl)
    ignored = transfer.GetToString()
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

    stopPanelAudio()

    entry = m.windowEntries[m.selectedIndex]
    entryId = getString(entry.id, "")
    streamUrl = getString(entry.streamUrl, "")
    thumbnailUrl = getString(entry.thumbnailUrl, "")
    if (thumbnailUrl = invalid or thumbnailUrl = "") and streamUrl = ""
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
    if m.versionLabel <> invalid
        m.versionLabel.visible = false
    end if
    if m.background <> invalid
        m.background.visible = false
    end if
    m.activeFullscreenPoster = m.fullscreenPosterA
    m.bufferFullscreenPoster = m.fullscreenPosterB
    m.videoUsesStream = Instr(1, LCase(streamUrl), ".m3u8") > 0
    m.videoStreamUrl = streamUrl
    m.fullscreenStreamingMode = normalizeStreamingMode(getString(entry.streamingMode, "Interacao"))
    m.showFullscreenDiagnosticsCount = m.showFullscreenDiagnosticsCount + 1
    if m.showFullscreenDiagnosticsCount <= 3
        ? "[BOOT] showFullscreen #"; m.showFullscreenDiagnosticsCount; " id="; entryId; " modo="; m.fullscreenStreamingMode; " usesStream="; m.videoUsesStream; " stream="; m.videoStreamUrl
    end if
    ? "[MODE] showFullscreen => id="; entryId; " modo="; m.fullscreenStreamingMode; " usesStream="; m.videoUsesStream; " stream="; m.videoStreamUrl
    m.fullscreenWindowId = entryId
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
    m.pendingInteractionAutoPlayWindowId = ""
    stopStagingFullscreenVideo()
    stopInactiveModePlayback(m.fullscreenStreamingMode, "showFullscreen")
    if m.videoUsesStream
        if m.fullscreenStreamingMode = "Interacao"
            if m.fullscreenVideo = invalid
                return
            end if
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
            m.cursorMarker.visible = true
            if m.fullscreenToastGroup <> invalid
                m.fullscreenToastGroup.visible = false
            end if
            if m.fullscreenToastLabel <> invalid
                m.fullscreenToastLabel.text = ""
            end if
            m.isFullscreenRefreshInFlight = false
            stopPanelRefresh()
            updateCursorMarker()
            startFullscreenStream()
            m.top.setFocus(true)
            return
        end if

        if m.fullscreenVideoMode = invalid
            return
        end if

        currentVideoState = LCase(getString(m.fullscreenVideoMode.state, ""))
        if m.fullscreenAssignedStreamUrl = m.videoStreamUrl and m.fullscreenWindowId = entryId and (currentVideoState = "playing" or currentVideoState = "buffering")
            ? "[HLS] showFullscreen ignorado: stream ja ativo => "; m.videoStreamUrl; " state="; currentVideoState
            m.fullscreenVideoMode.visible = true
            startFullscreenStream()
            m.top.setFocus(true)
            return
        end if

        if thumbnailUrl <> ""
            setFullscreenPoster(thumbnailUrl, entryId, true)
        else
            m.activeFullscreenPoster.visible = false
            m.bufferFullscreenPoster.visible = false
        end if

        content = CreateObject("roSGNode", "ContentNode")
        content.url = appendCacheBust(m.videoStreamUrl)
        content.streamFormat = "hls"
        content.title = getString(entry.title, "Painel")
        m.fullscreenPlayRequestCount = m.fullscreenPlayRequestCount + 1
        ? "[HLS] assign #"; m.fullscreenPlayRequestCount; " via showFullscreen => "; content.url
        ? "[HLS] play => "; content.url
        m.fullscreenVideoPendingRestart = true
        m.fullscreenAssignedStreamUrl = m.videoStreamUrl
        m.fullscreenVideoMode.content = content
        m.fullscreenVideoMode.control = "stop"
        m.fullscreenVideoMode.visible = true
        m.fullscreenVideoMode.control = "play"
        m.statusLabel.text = "Iniciando stream HLS do painel..."
    else
        if m.fullscreenVideo <> invalid
            m.fullscreenVideo.control = "stop"
            m.fullscreenVideo.content = invalid
            m.fullscreenVideo.visible = false
        end if
        if m.fullscreenVideoMode <> invalid
            m.fullscreenVideoMode.control = "stop"
            m.fullscreenVideoMode.content = invalid
            m.fullscreenVideoMode.visible = false
        end if
        m.fullscreenAssignedStreamUrl = ""
        m.fullscreenVideoPendingRestart = false
        setFullscreenPoster(thumbnailUrl, entryId, m.fullscreenStreamingMode <> "Video")
    end if
    m.cursorMarker.visible = true
    if m.fullscreenToastGroup <> invalid
        m.fullscreenToastGroup.visible = false
    end if
    if m.fullscreenToastLabel <> invalid
        m.fullscreenToastLabel.text = ""
    end if
    m.isFullscreenRefreshInFlight = false
    stopPanelRefresh()
    updateCursorMarker()
    if not m.videoUsesStream and m.fullscreenStreamingMode = "Interacao"
        syncInteractionDirectVideoOverlay(entry, true)
    else
        stopInteractionDirectVideoOverlay()
    end if
    if not m.videoUsesStream and m.fullscreenStreamingMode = "Video"
        m.statusLabel.text = "Carregando modo Video..."
    end if
    startFullscreenStream()
    m.top.setFocus(true)
end sub

sub stopVideoNode(node as object)
    if node <> invalid
        node.control = "stop"
        node.content = invalid
        node.visible = false
    end if
end sub

sub stopInteractionDirectVideoOverlay()
    if m.fullscreenInteractionOverlayVideo <> invalid
        m.fullscreenInteractionOverlayVideo.control = "stop"
        m.fullscreenInteractionOverlayVideo.content = invalid
        m.fullscreenInteractionOverlayVideo.visible = false
    end if
    if m.interactionOverlayViewport <> invalid
        m.interactionOverlayViewport.visible = false
    end if
    if m.interactionOverlayControlsTimer <> invalid
        m.interactionOverlayControlsTimer.control = "stop"
    end if
    if m.interactionOverlayControlsGroup <> invalid
        m.interactionOverlayControlsGroup.visible = false
    end if
    m.interactionOverlayAssignedStreamUrl = ""
    m.interactionOverlayControlsVisible = false
    m.interactionOverlayControlsFullscreen = false
    m.interactionOverlayBaseRect = invalid
    m.interactionOverlayCurrentRect = invalid
    m.interactionOverlayIntrinsicRect = invalid
    m.interactionOverlayPlayButtonRect = invalid
    m.interactionOverlayFullscreenButtonRect = invalid
    m.interactionOverlayProgressTrackRect = invalid
    m.interactionOverlayQualityButtonRect = invalid
    m.lastInteractionOverlayState = ""
    m.interactionOverlayControlsLastActivity = invalid
    m.interactionOverlayQualityOptions = []
    m.interactionOverlaySelectedQualityIndex = 0
    m.interactionOverlayPendingSeekPosition = invalid
    m.interactionOverlaySourceUrl = ""
    hideInteractionOverlayQualityMenu()
end sub

sub parkInteractionDirectVideoOverlay(reason as string)
    if m.fullscreenInteractionOverlayVideo = invalid
        return
    end if

    if m.interactionOverlayViewport <> invalid
        m.interactionOverlayViewport.visible = false
    end if
    if m.interactionOverlayControlsGroup <> invalid
        m.interactionOverlayControlsGroup.visible = false
    end if
    m.interactionOverlayControlsVisible = false
    m.interactionOverlayCurrentRect = invalid
    m.interactionOverlayPlayButtonRect = invalid
    m.interactionOverlayFullscreenButtonRect = invalid
    m.interactionOverlayProgressTrackRect = invalid
    m.interactionOverlayQualityButtonRect = invalid
    hideInteractionOverlayQualityMenu()
    ? "[OVERLAY] parked => reason="; reason
end sub

sub syncInteractionDirectVideoOverlay(entry as object, forceReload as boolean)
    if m.fullscreenInteractionOverlayVideo = invalid or entry = invalid
        return
    end if

    if normalizeStreamingMode(m.fullscreenStreamingMode) <> "Interacao"
        stopInteractionDirectVideoOverlay()
        return
    end if

    overlayEnabled = getBool(entry.directVideoOverlayEnabled, false)
    overlaySourceUrl = getString(entry.directVideoSourceUrl, "")
    overlayStreamUrl = getString(entry.directVideoStreamUrl, "")
    overlayStreamFormat = LCase(getString(entry.directVideoStreamFormat, ""))
    overlayQualityLabel = getString(entry.directVideoQualityLabel, "")
    initialUrl = LCase(getString(entry.initialUrl, ""))
    canPreserveOffscreenOverlay = m.interactionOverlayAssignedStreamUrl <> "" and (Instr(1, initialUrl, "youtube.com") > 0 or Instr(1, initialUrl, "youtu.be") > 0 or Instr(1, initialUrl, "youtube-nocookie.com") > 0)
    if overlaySourceUrl <> "" and overlaySourceUrl <> m.interactionOverlaySourceUrl
        m.interactionOverlayAutoMode = true
        m.interactionOverlayAutoDegradeLevel = 0
        m.interactionOverlayAutoBufferingCount = 0
    end if
    if not overlayEnabled or overlayStreamUrl = "" or overlayStreamFormat = ""
        if canPreserveOffscreenOverlay
            parkInteractionDirectVideoOverlay("overlay-disabled")
            return
        end if
        if forceReload
            ? "[OVERLAY] inativo => enabled="; overlayEnabled; " stream="; overlayStreamUrl; " format="; overlayStreamFormat
        end if
        stopInteractionDirectVideoOverlay()
        return
    end if

    left = clampNumber(getNumber(entry.directVideoNormalizedLeft, 0.0), 0.0, 1.0)
    top = clampNumber(getNumber(entry.directVideoNormalizedTop, 0.0), 0.0, 1.0)
    width = clampNumber(getNumber(entry.directVideoNormalizedWidth, 0.0), 0.0, 1.0 - left)
    height = clampNumber(getNumber(entry.directVideoNormalizedHeight, 0.0), 0.0, 1.0 - top)
    if width <= 0.01 or height <= 0.01
        if canPreserveOffscreenOverlay
            parkInteractionDirectVideoOverlay("offscreen")
            return
        end if
        ? "[OVERLAY] retangulo invalido => left="; left; " top="; top; " width="; width; " height="; height
        stopInteractionDirectVideoOverlay()
        return
    end if

    targetX = Int(1280 * left)
    targetY = Int(720 * top)
    targetWidth = Int(1280 * width)
    targetHeight = Int(720 * height)
    if targetWidth < 2
        targetWidth = 2
    end if
    if targetHeight < 2
        targetHeight = 2
    end if

    m.interactionOverlayBaseRect = {
        x: targetX
        y: targetY
        width: targetWidth
        height: targetHeight
    }
    if not m.interactionOverlayControlsFullscreen
        if targetY > 0 and targetWidth > 2 and targetHeight > 2
            m.interactionOverlayIntrinsicRect = {
                x: targetX
                y: targetY
                width: targetWidth
                height: targetHeight
            }
        else if m.interactionOverlayIntrinsicRect = invalid
            m.interactionOverlayIntrinsicRect = {
                x: targetX
                y: targetY
                width: targetWidth
                height: targetHeight
            }
        end if
    end if
    m.interactionOverlayQualityOptions = normalizeDirectVideoQualityOptions(entry.directVideoQualityOptions, overlayStreamUrl, overlayStreamFormat, overlayQualityLabel)
    selectedQualityIndex = findDirectVideoQualityOptionIndex(m.interactionOverlayQualityOptions, overlayStreamUrl, overlayQualityLabel)
    if m.interactionOverlayAutoMode
        selectedQualityIndex = resolveInteractionOverlayAutoQualityIndex(m.interactionOverlayQualityOptions)
    else if overlaySourceUrl <> "" and overlaySourceUrl = m.interactionOverlaySourceUrl and m.interactionOverlayAssignedStreamUrl <> ""
        preservedIndex = findDirectVideoQualityOptionIndex(m.interactionOverlayQualityOptions, m.interactionOverlayAssignedStreamUrl, "")
        if preservedIndex >= 0 and preservedIndex < m.interactionOverlayQualityOptions.Count()
            selectedQualityIndex = preservedIndex
        end if
    end if

    if selectedQualityIndex < 0 or selectedQualityIndex >= m.interactionOverlayQualityOptions.Count()
        selectedQualityIndex = 0
    end if

    selectedOption = m.interactionOverlayQualityOptions[selectedQualityIndex]
    selectedStreamUrl = getString(selectedOption.streamUrl, "")
    overlayStreamFormat = LCase(getString(selectedOption.streamFormat, overlayStreamFormat))
    selectedQualityLabel = getString(selectedOption.label, overlayQualityLabel)
    m.interactionOverlaySelectedQualityIndex = selectedQualityIndex
    applyInteractionOverlayLayout()

    if forceReload or m.interactionOverlayAssignedStreamUrl <> selectedStreamUrl or m.fullscreenInteractionOverlayVideo.content = invalid
        content = CreateObject("roSGNode", "ContentNode")
        content.url = appendCacheBust(selectedStreamUrl)
        content.streamFormat = overlayStreamFormat
        content.title = getString(entry.title, "YouTube direto")
        ? "[OVERLAY] play => "; content.url; " format="; overlayStreamFormat; " quality="; selectedQualityLabel; " rect="; targetX; ","; targetY; " "; targetWidth; "x"; targetHeight
        m.fullscreenInteractionOverlayVideo.content = content
        m.fullscreenInteractionOverlayVideo.control = "stop"
        m.fullscreenInteractionOverlayVideo.visible = true
        m.fullscreenInteractionOverlayVideo.control = "play"
        m.interactionOverlayAssignedStreamUrl = selectedStreamUrl
        m.interactionOverlaySourceUrl = overlaySourceUrl
        showInteractionOverlayControls("play")
        return
    end if

    m.fullscreenInteractionOverlayVideo.visible = true
    m.interactionOverlaySourceUrl = overlaySourceUrl
    refreshInteractionOverlayControls()
end sub

sub applyInteractionOverlayLayout()
    if m.fullscreenInteractionOverlayVideo = invalid or m.interactionOverlayBaseRect = invalid
        return
    end if

    visibleRect = m.interactionOverlayBaseRect
    intrinsicRect = m.interactionOverlayBaseRect
    renderRect = visibleRect
    useScreenViewport = false
    if m.interactionOverlayControlsFullscreen
        visibleRect = {
            x: 0
            y: 0
            width: 1280
            height: 720
        }
        intrinsicRect = visibleRect
        renderRect = visibleRect
    else if m.interactionOverlayIntrinsicRect <> invalid
        intrinsicCandidate = m.interactionOverlayIntrinsicRect
        sameWidth = Abs(intrinsicCandidate.width - visibleRect.width) <= 24
        heightShrunkAtTop = intrinsicCandidate.height > (visibleRect.height + 8)
        if heightShrunkAtTop and sameWidth
            intrinsicRect = {
                x: intrinsicCandidate.x
                y: intrinsicCandidate.y
                width: intrinsicCandidate.width
                height: intrinsicCandidate.height
            }
            renderRect = {
                x: intrinsicCandidate.x
                y: visibleRect.y - (intrinsicCandidate.height - visibleRect.height)
                width: intrinsicRect.width
                height: intrinsicRect.height
            }
            useScreenViewport = true
            ? "[OVERLAY] edge-illusion => visibleY="; visibleRect.y; " visibleH="; visibleRect.height; " intrinsicH="; intrinsicCandidate.height; " renderY="; renderRect.y
        end if
    end if

    m.interactionOverlayCurrentRect = visibleRect
    if m.interactionOverlayViewport <> invalid
        if useScreenViewport
            m.interactionOverlayViewport.translation = [0, 0]
            m.interactionOverlayViewport.clippingRect = [0, 0, 1280, 720]
        else
            m.interactionOverlayViewport.translation = [renderRect.x, renderRect.y]
            m.interactionOverlayViewport.clippingRect = [0, 0, renderRect.width, renderRect.height]
        end if
        m.interactionOverlayViewport.visible = true
    end if
    if useScreenViewport
        m.fullscreenInteractionOverlayVideo.translation = [renderRect.x, renderRect.y]
    else
        m.fullscreenInteractionOverlayVideo.translation = [0, 0]
    end if
    m.fullscreenInteractionOverlayVideo.width = renderRect.width
    m.fullscreenInteractionOverlayVideo.height = renderRect.height
    m.fullscreenInteractionOverlayVideo.visible = true
    layoutInteractionOverlayControls()
end sub

sub layoutInteractionOverlayControls()
    if m.interactionOverlayControlsGroup = invalid or m.interactionOverlayCurrentRect = invalid
        return
    end if

    rect = m.interactionOverlayCurrentRect
    panelHeight = 82
    if rect.height < 160
        panelHeight = 68
    end if

    groupY = rect.y + rect.height - panelHeight
    if groupY < rect.y
        groupY = rect.y
    end if

    m.interactionOverlayControlsGroup.translation = [rect.x, groupY]
    m.interactionOverlayControlsBackground.width = rect.width
    m.interactionOverlayControlsBackground.height = panelHeight

    trackX = 14
    trackY = 8
    trackWidth = rect.width - 28
    if trackWidth < 120
        trackWidth = 120
    end if
    buttonY = panelHeight - 54
    if buttonY < 14
        buttonY = 14
    end if

    leftCapsuleWidth = 214
    if rect.width < 700
        leftCapsuleWidth = 182
    end if

    rightCapsuleWidth = 340
    titleCapsuleX = trackX + leftCapsuleWidth + 10
    titleCapsuleWidth = rect.width - leftCapsuleWidth - rightCapsuleWidth - 34
    if titleCapsuleWidth < 160
        titleCapsuleWidth = 160
    end if
    rightCapsuleX = rect.width - rightCapsuleWidth - 14

    m.interactionOverlayProgressTrack.translation = [trackX, trackY]
    m.interactionOverlayProgressTrack.width = trackWidth
    m.interactionOverlayProgressFill.translation = [trackX, trackY]

    if m.interactionOverlayLeftCapsule <> invalid
        m.interactionOverlayLeftCapsule.translation = [trackX, buttonY]
        m.interactionOverlayLeftCapsule.width = leftCapsuleWidth
        m.interactionOverlayLeftCapsule.height = 40
    end if
    if m.interactionOverlayTitleCapsule <> invalid
        m.interactionOverlayTitleCapsule.translation = [titleCapsuleX, buttonY]
        m.interactionOverlayTitleCapsule.width = titleCapsuleWidth
        m.interactionOverlayTitleCapsule.height = 40
        m.interactionOverlayTitleCapsule.visible = false
    end if
    if m.interactionOverlayRightCapsule <> invalid
        m.interactionOverlayRightCapsule.translation = [rightCapsuleX, buttonY]
        m.interactionOverlayRightCapsule.width = rightCapsuleWidth
        m.interactionOverlayRightCapsule.height = 40
    end if

    m.interactionOverlayPlayButton.translation = [trackX + 10, buttonY + 6]
    m.interactionOverlayPlayLabel.translation = [trackX + 10, buttonY + 6]
    m.interactionOverlayTimeLabel.translation = [trackX + 70, buttonY + 10]
    if m.interactionOverlayTitleLabel <> invalid
        m.interactionOverlayTitleLabel.translation = [titleCapsuleX + 14, buttonY + 8]
        m.interactionOverlayTitleLabel.width = titleCapsuleWidth - 28
        m.interactionOverlayTitleLabel.visible = false
    end if
    if m.interactionOverlayQualityButton <> invalid
        m.interactionOverlayQualityButton.translation = [rightCapsuleX + 12, buttonY + 6]
        m.interactionOverlayQualityButton.width = 204
    end if
    if m.interactionOverlayQualityLabel <> invalid
        m.interactionOverlayQualityLabel.translation = [rightCapsuleX + 12, buttonY + 6]
        m.interactionOverlayQualityLabel.width = 204
    end if
    m.interactionOverlayFullscreenButton.translation = [rightCapsuleX + 228, buttonY + 6]
    m.interactionOverlayFullscreenLabel.translation = [rightCapsuleX + 228, buttonY + 6]

    m.interactionOverlayProgressTrackRect = {
        x: rect.x + trackX
        y: groupY + trackY
        width: trackWidth
        height: 10
    }
    m.interactionOverlayPlayButtonRect = {
        x: rect.x + trackX + 10
        y: groupY + buttonY + 6
        width: 52
        height: 28
    }
    m.interactionOverlayQualityButtonRect = {
        x: rect.x + rightCapsuleX + 12
        y: groupY + buttonY + 6
        width: 204
        height: 28
    }
    m.interactionOverlayFullscreenButtonRect = {
        x: rect.x + rightCapsuleX + 228
        y: groupY + buttonY + 6
        width: 92
        height: 28
    }
    layoutInteractionOverlayQualityMenu(rect, groupY, rightCapsuleX + 12, buttonY + 6)
    refreshInteractionOverlayControls()
end sub

sub refreshInteractionOverlayControls()
    if m.interactionOverlayControlsGroup = invalid or m.fullscreenInteractionOverlayVideo = invalid or m.interactionOverlayCurrentRect = invalid
        return
    end if

    state = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    durationValue = getNumber(m.fullscreenInteractionOverlayVideo.duration, 0.0)
    positionValue = getNumber(m.fullscreenInteractionOverlayVideo.position, 0.0)
    fillWidth = 0
    if durationValue > 0 and m.interactionOverlayProgressTrack <> invalid
        ratio = clampNumber(positionValue / durationValue, 0.0, 1.0)
        fillWidth = Int(m.interactionOverlayProgressTrack.width * ratio)
    end if

    if m.interactionOverlayProgressFill <> invalid
        m.interactionOverlayProgressFill.width = fillWidth
    end if

    if m.interactionOverlayTimeLabel <> invalid
        m.interactionOverlayTimeLabel.text = formatOverlayTime(positionValue) + " / " + formatOverlayTime(durationValue)
    end if

    if m.interactionOverlayTitleLabel <> invalid
        m.interactionOverlayTitleLabel.text = ""
    end if

    if m.interactionOverlayPlayLabel <> invalid
        if state = "playing" or state = "buffering"
            m.interactionOverlayPlayLabel.text = "II"
        else
            m.interactionOverlayPlayLabel.text = ">"
        end if
    end if

    if m.interactionOverlayFullscreenLabel <> invalid
        if m.interactionOverlayControlsFullscreen
            m.interactionOverlayFullscreenLabel.text = "< >"
        else
            m.interactionOverlayFullscreenLabel.text = "[ ]"
        end if
    end if

    if m.interactionOverlayQualityLabel <> invalid
        m.interactionOverlayQualityLabel.text = getCurrentInteractionOverlayQualityButtonLabel()
    end if

    playHover = isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayPlayButtonRect) and m.interactionOverlayControlsVisible
    qualityHover = isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayQualityButtonRect) and m.interactionOverlayControlsVisible
    fullscreenHover = isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayFullscreenButtonRect) and m.interactionOverlayControlsVisible
    if m.interactionOverlayPlayButton <> invalid
        if playHover
            m.interactionOverlayPlayButton.color = "0xFF3B5C55"
        else
            m.interactionOverlayPlayButton.color = "0xFFFFFF18"
        end if
    end if
    if m.interactionOverlayFullscreenButton <> invalid
        if fullscreenHover
            m.interactionOverlayFullscreenButton.color = "0xFF3B5C55"
        else
            m.interactionOverlayFullscreenButton.color = "0xFFFFFF18"
        end if
    end if
    if m.interactionOverlayQualityButton <> invalid
        if qualityHover or m.interactionOverlayQualityMenuVisible
            m.interactionOverlayQualityButton.color = "0xFF3B5C55"
        else
            m.interactionOverlayQualityButton.color = "0xFFFFFF18"
        end if
    end if

    refreshInteractionOverlayQualityMenu()
end sub

sub layoutInteractionOverlayQualityMenu(rect as object, groupY as integer, buttonX as integer, buttonY as integer)
    if m.interactionOverlayQualityMenuGroup = invalid or m.interactionOverlayQualityMenuBackground = invalid
        return
    end if

    m.interactionOverlayQualityMenuRects = []
    m.interactionOverlayQualityMenuNodeIndices = []

    itemCount = getInteractionOverlayQualityMenuItemCount()
    if itemCount <= 0
        m.interactionOverlayQualityMenuGroup.visible = false
        return
    end if

    menuWidth = 214
    menuHeight = (itemCount * 32) + 6
    menuX = buttonX
    menuY = buttonY - menuHeight - 8
    if menuY < 8
        menuY = 8
    end if

    m.interactionOverlayQualityMenuGroup.translation = [menuX, menuY]
    m.interactionOverlayQualityMenuBackground.width = menuWidth
    m.interactionOverlayQualityMenuBackground.height = menuHeight

    globalX = rect.x + menuX
    globalY = groupY + menuY
    for i = 0 to m.interactionOverlayQualityMenuItemBackgrounds.Count() - 1
        itemBg = m.interactionOverlayQualityMenuItemBackgrounds[i]
        itemLabel = m.interactionOverlayQualityMenuItemLabels[i]
        if itemBg <> invalid and itemLabel <> invalid
            if i < itemCount
                itemY = 3 + (i * 32)
                itemBg.translation = [4, itemY]
                itemBg.width = menuWidth - 8
                itemBg.height = 30
                itemBg.visible = true

                itemLabel.translation = [4, itemY]
                itemLabel.width = menuWidth - 8
                itemLabel.height = 30
                itemLabel.visible = true

                m.interactionOverlayQualityMenuRects.Push({
                    x: globalX + 4
                    y: globalY + itemY
                    width: menuWidth - 8
                    height: 30
                })
                m.interactionOverlayQualityMenuNodeIndices.Push(i)
            else
                itemBg.visible = false
                itemLabel.visible = false
            end if
        end if
    end for

    m.interactionOverlayQualityMenuGroup.visible = m.interactionOverlayQualityMenuVisible and m.interactionOverlayControlsVisible
end sub

sub refreshInteractionOverlayQualityMenu()
    if m.interactionOverlayQualityMenuGroup = invalid
        return
    end if

    itemCount = getInteractionOverlayQualityMenuItemCount()
    m.interactionOverlayQualityMenuGroup.visible = m.interactionOverlayQualityMenuVisible and m.interactionOverlayControlsVisible and itemCount > 0
    if itemCount <= 0
        return
    end if

    selectedMenuIndex = getInteractionOverlayQualityMenuSelectedIndex()
    for i = 0 to m.interactionOverlayQualityMenuItemBackgrounds.Count() - 1
        itemBg = m.interactionOverlayQualityMenuItemBackgrounds[i]
        itemLabel = m.interactionOverlayQualityMenuItemLabels[i]
        if itemBg <> invalid and itemLabel <> invalid
            if i >= itemCount
                itemBg.visible = false
                itemLabel.visible = false
            else
                itemBg.visible = true
                itemLabel.visible = true
                itemLabel.text = getInteractionOverlayQualityMenuLabel(i)

                hover = i < m.interactionOverlayQualityMenuRects.Count() and isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayQualityMenuRects[i]) and m.interactionOverlayQualityMenuVisible
                if i = selectedMenuIndex
                    itemBg.color = "0xE11D48CC"
                else if hover
                    itemBg.color = "0xFF3B5C55"
                else
                    itemBg.color = "0xFFFFFF18"
                end if
            end if
        end if
    end for
end sub

function fitOverlayTitle(textValue as string) as string
    safeText = getString(textValue, "")
    if Len(safeText) <= 52
        return safeText
    end if
    return Left(safeText, 49) + "..."
end function

function formatOverlayTime(totalSeconds as dynamic) as string
    value = Int(getNumber(totalSeconds, 0.0))
    if value < 0
        value = 0
    end if
    minutes = Int(value / 60)
    seconds = value Mod 60
    minuteText = minutes.ToStr()
    secondText = seconds.ToStr()
    if Len(secondText) < 2
        secondText = "0" + secondText
    end if
    return minuteText + ":" + secondText
end function

sub showInteractionOverlayControls(reason as string)
    if m.interactionOverlayControlsGroup = invalid or not isInteractionOverlayActive()
        return
    end if

    if m.interactionOverlayControlsLastActivity = invalid
        m.interactionOverlayControlsLastActivity = CreateObject("roTimespan")
    end if
    m.interactionOverlayControlsLastActivity.Mark()
    m.interactionOverlayControlsVisible = true
    m.interactionOverlayControlsGroup.visible = true
    if m.interactionOverlayQualityMenuVisible and m.interactionOverlayQualityMenuGroup <> invalid
        m.interactionOverlayQualityMenuGroup.visible = true
    end if
    ? "[OVERLAY] controls => show reason="; reason
    refreshInteractionOverlayControls()

    if m.interactionOverlayControlsTimer <> invalid
        m.interactionOverlayControlsTimer.control = "stop"
        m.interactionOverlayControlsTimer.control = "start"
    end if
end sub

sub hideInteractionOverlayControls()
    if m.interactionOverlayQualityMenuVisible
        return
    end if
    m.interactionOverlayControlsVisible = false
    if m.interactionOverlayControlsGroup <> invalid
        m.interactionOverlayControlsGroup.visible = false
    end if
    ? "[OVERLAY] controls => hide"
    if m.interactionOverlayControlsTimer <> invalid
        m.interactionOverlayControlsTimer.control = "stop"
    end if
end sub

function isInteractionOverlayActive() as boolean
    return normalizeStreamingMode(m.fullscreenStreamingMode) = "Interacao" and m.fullscreenInteractionOverlayVideo <> invalid and m.fullscreenInteractionOverlayVideo.content <> invalid and m.interactionOverlayAssignedStreamUrl <> ""
end function

function shouldCaptureInteractionOverlayPointerInput() as boolean
    return isInteractionOverlayActive() and m.interactionOverlayCurrentRect <> invalid and isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayCurrentRect)
end function

sub notifyInteractionOverlayPointerActivity()
    hoveredMenuIndex = getInteractionOverlayHoveredQualityMenuIndex()
    if hoveredMenuIndex >= 0
        m.interactionOverlayQualityMenuSelectedIndex = hoveredMenuIndex
        showInteractionOverlayControls("quality-menu-hover")
        return
    end if

    if not shouldCaptureInteractionOverlayPointerInput()
        return
    end if

    showInteractionOverlayControls("hover")
end sub

function getInteractionOverlayControlHitTarget() as string
    if not isInteractionOverlayActive() or not m.interactionOverlayControlsVisible
        return ""
    end if

    if m.interactionOverlayQualityMenuVisible
        for i = 0 to m.interactionOverlayQualityMenuRects.Count() - 1
            if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayQualityMenuRects[i])
                return "quality-menu-" + i.ToStr()
            end if
        end for
    end if

    if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayPlayButtonRect)
        return "play"
    end if
    if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayFullscreenButtonRect)
        return "fullscreen"
    end if
    if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayQualityButtonRect)
        return "quality"
    end if
    if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayProgressTrackRect)
        return "progress"
    end if
    return ""
end function

function isPointInsideRect(x as integer, y as integer, rect as dynamic) as boolean
    if rect = invalid
        return false
    end if
    return x >= rect.x and x <= (rect.x + rect.width) and y >= rect.y and y <= (rect.y + rect.height)
end function

sub handleInteractionOverlayControlHit(target as string)
    if Left(target, 13) = "quality-menu-"
        indexValue = Val(Mid(target, 14))
        ? "[OVERLAY] quality-menu => hit index="; indexValue
        selectInteractionOverlayQualityMenuIndex(indexValue, "pointer")
        showInteractionOverlayControls("quality-select")
        return
    end if

    if target = "play"
        toggleInteractionOverlayPlayback()
    else if target = "quality"
        toggleInteractionOverlayQualityMenu("button")
    else if target = "fullscreen"
        toggleInteractionOverlayFullscreen()
    else if target = "progress"
        seekInteractionOverlayFromCursor()
    end if
    showInteractionOverlayControls(target)
end sub

sub toggleInteractionOverlayPlayback()
    if not isInteractionOverlayActive()
        return
    end if

    state = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    if state = "playing" or state = "buffering"
        ? "[OVERLAY] pause => state="; state
        m.fullscreenInteractionOverlayVideo.control = "pause"
    else if state = "paused"
        ? "[OVERLAY] resume => state="; state
        m.fullscreenInteractionOverlayVideo.control = "resume"
    else
        ? "[OVERLAY] play-toggle => state="; state
        m.fullscreenInteractionOverlayVideo.control = "play"
    end if
end sub

sub toggleInteractionOverlayFullscreen()
    if not isInteractionOverlayActive()
        return
    end if

    m.interactionOverlayControlsFullscreen = not m.interactionOverlayControlsFullscreen
    ? "[OVERLAY] fullscreen => "; m.interactionOverlayControlsFullscreen
    applyInteractionOverlayLayout()
    showInteractionOverlayControls("fullscreen")
end sub

sub cycleInteractionOverlayQuality()
    cycleInteractionOverlayQualityByDelta(1, "cycle")
end sub

sub cycleInteractionOverlayQualityByDelta(delta as integer, reason as string)
    if not isInteractionOverlayActive()
        return
    end if

    if m.interactionOverlayQualityOptions = invalid or m.interactionOverlayQualityOptions.Count() <= 1
        ? "[OVERLAY] quality => unavailable"
        showInteractionOverlayControls("quality-unavailable")
        return
    end if

    m.interactionOverlayAutoMode = false
    m.interactionOverlayAutoDegradeLevel = 0
    m.interactionOverlayAutoBufferingCount = 0
    nextIndex = m.interactionOverlaySelectedQualityIndex + delta
    optionCount = m.interactionOverlayQualityOptions.Count()
    if optionCount <= 0
        return
    end if
    while nextIndex < 0
        nextIndex = nextIndex + optionCount
    end while
    while nextIndex >= optionCount
        nextIndex = nextIndex - optionCount
    end while

    applyInteractionOverlayQualityOption(nextIndex, reason)
end sub

sub toggleInteractionOverlayQualityMenu(reason as string)
    if not isInteractionOverlayActive()
        return
    end if

    if m.interactionOverlayQualityMenuVisible
        hideInteractionOverlayQualityMenu()
        showInteractionOverlayControls("quality-menu-close")
        return
    end if

    m.interactionOverlayQualityMenuVisible = true
    m.interactionOverlayQualityMenuSelectedIndex = getInteractionOverlayQualityMenuSelectedIndex()
    ? "[OVERLAY] quality-menu => open reason="; reason
    showInteractionOverlayControls("quality-menu-open")
    refreshInteractionOverlayControls()
end sub

sub hideInteractionOverlayQualityMenu()
    m.interactionOverlayQualityMenuVisible = false
    if m.interactionOverlayQualityMenuGroup <> invalid
        m.interactionOverlayQualityMenuGroup.visible = false
    end if
end sub

sub moveInteractionOverlayQualityMenuSelection(delta as integer)
    itemCount = getInteractionOverlayQualityMenuItemCount()
    if itemCount <= 0
        return
    end if

    if not m.interactionOverlayQualityMenuVisible
        toggleInteractionOverlayQualityMenu("direction")
        return
    end if

    nextIndex = m.interactionOverlayQualityMenuSelectedIndex + delta
    if nextIndex < 0
        nextIndex = 0
    else if nextIndex >= itemCount
        nextIndex = itemCount - 1
    end if
    m.interactionOverlayQualityMenuSelectedIndex = nextIndex
    showInteractionOverlayControls("quality-menu-nav")
    refreshInteractionOverlayControls()
end sub

sub selectInteractionOverlayQualityMenuIndex(menuIndex as integer, reason as string)
    itemCount = getInteractionOverlayQualityMenuItemCount()
    if menuIndex < 0 or menuIndex >= itemCount
        return
    end if

    ? "[OVERLAY] quality-menu => select index="; menuIndex; " reason="; reason; " label="; getInteractionOverlayQualityMenuLabel(menuIndex)
    m.interactionOverlayQualityMenuSelectedIndex = menuIndex
    hideInteractionOverlayQualityMenu()
    if menuIndex = 0
        m.interactionOverlayAutoMode = true
        m.interactionOverlayAutoDegradeLevel = 0
        m.interactionOverlayAutoBufferingCount = 0
        autoIndex = resolveInteractionOverlayAutoQualityIndex(m.interactionOverlayQualityOptions)
        applyInteractionOverlayQualityOption(autoIndex, "auto-" + reason)
    else
        m.interactionOverlayAutoMode = false
        m.interactionOverlayAutoDegradeLevel = 0
        m.interactionOverlayAutoBufferingCount = 0
        applyInteractionOverlayQualityOption(menuIndex - 1, "manual-" + reason)
    end if
end sub

sub applyInteractionOverlayQualityOption(optionIndex as integer, reason as string)
    if not isInteractionOverlayActive()
        return
    end if

    if optionIndex < 0 or optionIndex >= m.interactionOverlayQualityOptions.Count()
        return
    end if

    option = m.interactionOverlayQualityOptions[optionIndex]
    streamUrl = getString(option.streamUrl, "")
    streamFormat = LCase(getString(option.streamFormat, ""))
    qualityLabel = getString(option.label, "")
    if streamUrl = "" or streamFormat = ""
        return
    end if

    if streamUrl = m.interactionOverlayAssignedStreamUrl
        m.interactionOverlaySelectedQualityIndex = optionIndex
        refreshInteractionOverlayControls()
        return
    end if

    resumeState = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    currentPosition = getNumber(m.fullscreenInteractionOverlayVideo.position, 0.0)
    shouldResume = resumeState = "playing" or resumeState = "buffering" or resumeState = "paused"

    m.interactionOverlaySelectedQualityIndex = optionIndex
    if currentPosition > 0
        m.interactionOverlayPendingSeekPosition = currentPosition
    else
        m.interactionOverlayPendingSeekPosition = invalid
    end if
    m.interactionOverlayAssignedStreamUrl = streamUrl
    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(streamUrl)
    content.streamFormat = streamFormat
    content.title = qualityLabel
    m.fullscreenInteractionOverlayVideo.control = "stop"
    m.fullscreenInteractionOverlayVideo.content = invalid
    ? "[OVERLAY] quality => "; qualityLabel; " format="; streamFormat; " reason="; reason; " resume="; shouldResume; " pos="; currentPosition
    m.fullscreenInteractionOverlayVideo.content = content
    m.fullscreenInteractionOverlayVideo.visible = true
    m.fullscreenInteractionOverlayVideo.control = "play"
    showInteractionOverlayControls("quality")
end sub

sub seekInteractionOverlayFromCursor()
    if not isInteractionOverlayActive() or m.interactionOverlayProgressTrackRect = invalid
        return
    end if

    durationValue = getNumber(m.fullscreenInteractionOverlayVideo.duration, 0.0)
    if durationValue <= 0
        return
    end if

    ratio = clampNumber((m.cursorX - m.interactionOverlayProgressTrackRect.x) / m.interactionOverlayProgressTrackRect.width, 0.0, 1.0)
    targetPosition = durationValue * ratio
    ? "[OVERLAY] seek => "; targetPosition
    applyInteractionOverlaySeek(targetPosition, "cursor")
    refreshInteractionOverlayControls()
end sub

sub applyInteractionOverlaySeek(targetPosition as double, reason as string)
    if not isInteractionOverlayActive()
        return
    end if

    state = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    shouldResume = state = "playing" or state = "buffering"
    m.interactionOverlayPendingSeekPosition = targetPosition
    ? "[OVERLAY] seek-apply => "; reason; " pos="; targetPosition; " resume="; shouldResume
    if shouldResume
        m.fullscreenInteractionOverlayVideo.control = "pause"
    end if
    m.fullscreenInteractionOverlayVideo.seek = targetPosition
    if shouldResume
        m.fullscreenInteractionOverlayVideo.control = "resume"
    end if
end sub

function handleInteractionOverlayTransportShortcut(direction as string) as boolean
    if not isInteractionOverlayActive()
        return false
    end if

    if direction = "toggle"
        toggleInteractionOverlayPlayback()
    else
        durationValue = getNumber(m.fullscreenInteractionOverlayVideo.duration, 0.0)
        if m.interactionOverlayPendingSeekPosition <> invalid
            positionValue = getNumber(m.interactionOverlayPendingSeekPosition, 0.0)
        else
            positionValue = getNumber(m.fullscreenInteractionOverlayVideo.position, 0.0)
        end if

        if direction = "backward"
            targetPosition = positionValue - m.interactionOverlaySeekStepSeconds
        else
            targetPosition = positionValue + m.interactionOverlaySeekStepSeconds
        end if
        if durationValue > 0
            targetPosition = clampNumber(targetPosition, 0.0, durationValue)
        else if targetPosition < 0
            targetPosition = 0
        end if
        ? "[OVERLAY] seek-shortcut => "; direction; " pos="; targetPosition
        applyInteractionOverlaySeek(targetPosition, direction)
    end if

    showInteractionOverlayControls("transport")
    return true
end function

sub stopInactiveModePlayback(activeMode as string, reason as string)
    if normalizeStreamingMode(activeMode) = "Interacao"
        if m.fullscreenVideoMode <> invalid
            ? "[MODE] stop video-mode player => reason="; reason
            stopVideoNode(m.fullscreenVideoMode)
        end if
        stopStagingFullscreenVideo()
        return
    end if

    if m.fullscreenVideo <> invalid
        ? "[MODE] stop interaction player => reason="; reason
        stopVideoNode(m.fullscreenVideo)
    end if
    stopInteractionDirectVideoOverlay()
    stopPanelAudio()
end sub

sub hideFullscreen()
    if m.lockedFullscreen
        return
    end if

    m.isFullscreen = false
    clearPendingEditableActivation()
    stopHeldDirection()
    stopHeldRemoteCommand()
    stopFullscreenStream()
    teardownFullscreenPlayback("hideFullscreen")
    m.fullscreenStreamingMode = "Interacao"
    m.fullscreenWindowId = ""
    m.modeSwitchState = "idle"
    m.pendingModeSwitchWindowId = ""
    m.pendingModeSwitchCurrentMode = ""
    m.pendingModeSwitchTargetMode = ""
    m.pendingModeSwitchAckSent = false
    m.pendingAutoFullscreenWindowId = ""
    m.pendingAutoFullscreenStreamUrl = ""
    m.pendingAutoFullscreenSeenCount = 0
    m.lastInteractionOkTimespan = invalid
    m.fullscreenOkActive = false
    m.pendingInteractionAutoPlayWindowId = ""
    m.interactionOverlayAssignedStreamUrl = ""
    m.interactionOverlaySourceUrl = ""
    m.scrollModeEnabled = false
    m.fullscreenPosterWindowId = ""
    m.fullscreenPosterSourceUrl = ""
    if m.fullscreenVideoWatchTimer <> invalid
        m.fullscreenVideoWatchTimer.control = "stop"
    end if
    if m.scrollModeToastTimer <> invalid
        m.scrollModeToastTimer.control = "stop"
    end if
    m.fullscreenPosterA.visible = false
    m.fullscreenPosterB.visible = false
    m.cursorMarker.visible = false
    if m.fullscreenToastGroup <> invalid
        m.fullscreenToastGroup.visible = false
    end if
    if m.fullscreenToastLabel <> invalid
        m.fullscreenToastLabel.text = ""
    end if
    m.titleLabel.visible = true
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    if m.versionLabel <> invalid
        m.versionLabel.visible = true
    end if
    if m.background <> invalid
        m.background.visible = true
    end if
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
        ? "[AUDIO] startPanelAudio ignorado: sem audioUrl para "; getString(entry.id, "")
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
    ? "[AUDIO] startPanelAudio => session="; m.audioSessionId; " mode=scenegraph usesHls="; m.audioUsesHls; " url="; content.url
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
    if not m.audioUsesHls and m.audioFallbackTimer <> invalid
        m.audioFallbackTimer.control = "stop"
        m.audioFallbackTimer.control = "start"
    end if
end sub

sub onFullscreenInteractionVideoStateChanged()
    handleFullscreenVideoStateChanged("Interacao")
end sub

sub onInteractionOverlayVideoStateChanged()
    if m.fullscreenInteractionOverlayVideo = invalid
        return
    end if

    state = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    if state = "playing" or state = "finished" or state = "stopped"
        m.interactionOverlayPendingSeekPosition = invalid
    end if
    if state = "" or state = m.lastInteractionOverlayState
        return
    end if

    m.lastInteractionOverlayState = state
    ? "[OVERLAY] state => "; state; " pos="; getNumber(m.fullscreenInteractionOverlayVideo.position, 0.0)
    if m.interactionOverlayAutoMode
        if state = "playing"
            m.interactionOverlayAutoBufferingCount = 0
        else if state = "buffering"
            m.interactionOverlayAutoBufferingCount = m.interactionOverlayAutoBufferingCount + 1
            if m.interactionOverlayAutoBufferingCount >= 3
                m.interactionOverlayAutoBufferingCount = 0
                if tryDegradeInteractionOverlayAutoQuality("buffering")
                    return
                end if
            end if
        else if state = "error"
            m.interactionOverlayAutoBufferingCount = 0
            if tryDegradeInteractionOverlayAutoQuality("error")
                return
            end if
        end if
    end if
    refreshInteractionOverlayControls()
    if state <> "playing"
        showInteractionOverlayControls("state")
    end if
end sub

sub onFullscreenVideoModeStateChanged()
    handleFullscreenVideoStateChanged("Video")
end sub

sub handleFullscreenVideoStateChanged(mode as string)
    node = invalid
    if normalizeStreamingMode(mode) = "Interacao"
        node = m.fullscreenVideo
    else
        node = m.fullscreenVideoMode
    end if

    if node = invalid or not m.videoUsesStream or normalizeStreamingMode(m.fullscreenStreamingMode) <> normalizeStreamingMode(mode)
        return
    end if

    state = LCase(getString(node.state, ""))
    if state = ""
        return
    end if

    ? "[HLS] video state => "; mode; " => "; state

    if normalizeStreamingMode(mode) = "Interacao"
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
                node.content = content
                node.control = "stop"
                node.control = "play"
                m.statusLabel.text = "Reiniciando stream HLS do painel..."
            end if
        end if
        return
    end if

    positionValue = node.position
    if positionValue = invalid
        positionValue = -1
    end if
    reportVideoDiag("video-state", "state=" + state + ":pos=" + positionValue.ToStr())

    if state = "playing"
        m.fullscreenVideoPendingRestart = false
        m.fullscreenVideoStallCount = 0
        m.activeFullscreenPoster.visible = false
        m.bufferFullscreenPoster.visible = false
        m.statusLabel.text = "Stream HLS do painel em reproducao"
    else if state = "buffering"
        m.statusLabel.text = "Bufferizando stream HLS do painel..."
    else if state = "error"
        m.fullscreenVideoPendingRestart = false
        m.statusLabel.text = "Falha no stream HLS; mantendo preview por snapshots"
    else if state = "finished" or state = "stopped"
        ? "[HLS] estado final/transitorio em Video => "; state
        m.statusLabel.text = "Aguardando retomada do stream HLS..."
    end if
end sub

sub onFullscreenVideoWatchTimerFire()
    if not m.isFullscreen or not m.videoUsesStream
        return
    end if

    if normalizeStreamingMode(m.fullscreenStreamingMode) <> "Video"
        return
    end if

    node = invalid
    if normalizeStreamingMode(m.fullscreenStreamingMode) = "Video"
        node = m.fullscreenVideoMode
    else
        node = m.fullscreenVideo
    end if

    if node = invalid
        return
    end if

    state = LCase(getString(node.state, ""))
    position = node.position
    if position = invalid
        position = -1
    end if

    if state = "playing"
        if position > m.fullscreenVideoLastPosition
            m.fullscreenVideoLastPosition = position
            m.fullscreenVideoStallCount = 0
        end if
        return
    end if

    if state = "buffering"
        if position <= m.fullscreenVideoLastPosition
            m.fullscreenVideoStallCount = m.fullscreenVideoStallCount + 1
        else
            m.fullscreenVideoLastPosition = position
            m.fullscreenVideoStallCount = 0
        end if
    else
        m.fullscreenVideoStallCount = 0
        return
    end if

    if normalizeStreamingMode(m.fullscreenStreamingMode) = "Video" and m.fullscreenVideoStallCount >= 5
        restartFullscreenVideoAtLiveEdge("stall")
    end if
end sub

sub restartFullscreenVideoAtLiveEdge(reason as string)
    if not m.isFullscreen or not m.videoUsesStream or m.videoStreamUrl = ""
        return
    end if

    node = invalid
    if normalizeStreamingMode(m.fullscreenStreamingMode) = "Video"
        node = m.fullscreenVideoMode
    else
        node = m.fullscreenVideo
    end if

    if node = invalid
        return
    end if

    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(m.videoStreamUrl)
    content.streamFormat = "hls"
    content.title = "Painel"
    ? "[HLS] restart live-edge => reason="; reason; " url="; content.url
    m.fullscreenVideoPendingRestart = true
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
    reportVideoDiag("video-restart", "reason=" + reason)
    node.content = content
    node.control = "stop"
    node.visible = true
    node.control = "play"
    m.statusLabel.text = "Atualizando stream ao vivo..."
end sub

sub startStagingFullscreenVideo(entry as object, streamUrl as string, targetMode as string)
    if m.fullscreenVideoMode = invalid or streamUrl = ""
        return
    end if

    if m.stagingVideoStreamUrl = streamUrl and m.stagingVideoTargetMode = targetMode
        return
    end if

    stopInactiveModePlayback("Video", "startStagingFullscreenVideo")
    m.stagingVideoTargetMode = targetMode
    m.stagingVideoWindowId = getString(entry.id, "")
    m.stagingVideoStreamUrl = streamUrl
    windowId = m.stagingVideoWindowId

    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(streamUrl)
    content.streamFormat = "hls"
    content.title = getString(entry.title, "Painel")
    ? "[HLS] direct video play => "; content.url
    m.fullscreenVideoPendingRestart = true
    m.fullscreenVideoMode.content = content
    m.fullscreenVideoMode.control = "stop"
    m.fullscreenVideoMode.visible = true
    m.fullscreenVideoMode.control = "play"
    stopStagingFullscreenVideo()
    m.videoUsesStream = true
    m.videoStreamUrl = streamUrl
    m.fullscreenAssignedStreamUrl = streamUrl
    m.fullscreenStreamingMode = targetMode
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
    m.modeSwitchState = "idle"
    m.pendingModeSwitchWindowId = ""
    m.pendingModeSwitchCurrentMode = ""
    m.pendingModeSwitchTargetMode = ""
    m.pendingModeSwitchAckSent = false
    notifyPendingModeSwitchReady(windowId, targetMode)
    reportVideoDiag("video-assign", "target=" + targetMode)
    m.statusLabel.text = "Iniciando stream HLS do painel..."
end sub

sub stopStagingFullscreenVideo()
    m.stagingVideoTargetMode = ""
    m.stagingVideoWindowId = ""
    m.stagingVideoStreamUrl = ""
    if m.fullscreenVideoStage <> invalid
        m.fullscreenVideoStage.control = "stop"
        m.fullscreenVideoStage.content = invalid
        m.fullscreenVideoStage.visible = false
    end if
end sub

sub teardownFullscreenPlayback(reason as string)
    ? "[MODE] teardown => reason="; reason; " currentMode="; m.fullscreenStreamingMode; " pendingState="; m.modeSwitchState; " videoUsesStream="; m.videoUsesStream; " videoUrl="; m.videoStreamUrl; " assigned="; m.fullscreenAssignedStreamUrl; " audioSession="; m.audioSessionId

    stopPanelAudio()
    stopStagingFullscreenVideo()
    stopVideoNode(m.fullscreenVideo)
    stopVideoNode(m.fullscreenVideoMode)
    stopInteractionDirectVideoOverlay()

    m.fullscreenAssignedStreamUrl = ""
    m.fullscreenVideoPendingRestart = false
    m.videoUsesStream = false
    m.videoStreamUrl = ""
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0
end sub

sub stopPanelAudio()
    if m.panelAudioNode = invalid
        return
    end if

    ? "[AUDIO] stopPanelAudio => session="; m.audioSessionId; " mode="; m.audioMode; " usesHls="; m.audioUsesHls; " chunkUrl="; m.audioChunkUrl; " videoUsesStream="; m.videoUsesStream
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
    if m.videoUsesStream
        ? "[AUDIO] bloqueado em onPanelAudioStateChanged porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

    if m.panelAudioNode = invalid or m.audioUsesHls
        return
    end if

    state = LCase(getString(m.panelAudioNode.state, ""))
    if state = ""
        return
    end if

    ? "[AUDIO] panelAudioNode state => "; state; " session="; m.audioSessionId; " mode="; m.audioMode

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
    if m.videoUsesStream
        ? "[AUDIO] bloqueado em onPanelAudioVideoStateChanged porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

    if m.panelAudioVideo = invalid or not m.audioUsesHls
        return
    end if

    state = LCase(getString(m.panelAudioVideo.state, ""))
    if state = ""
        return
    end if

    ? "[AUDIO] panelAudioVideo state => "; state; " session="; m.audioSessionId; " mode="; m.audioMode

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
    if m.videoUsesStream
        ? "[AUDIO] onAudioHlsRestartTimerFire bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

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
    if m.videoUsesStream
        ? "[AUDIO] onAudioRetryTimerFire bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

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
    if m.videoUsesStream
        ? "[AUDIO] restartPanelAudio bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

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
    if m.videoUsesStream
        ? "[AUDIO] onAudioFallbackTimerFire bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

    if m.audioUsesHls
        return
    end if

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
    if m.videoUsesStream
        ? "[AUDIO] startLegacyPanelAudio bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

    if m.audioChunkUrl = ""
        return
    end if

    m.audioMode = "legacy"
    m.audioUsesHls = false
    ? "[AUDIO] startLegacyPanelAudio => session="; m.audioSessionId; " url="; m.audioChunkUrl
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
    if m.videoUsesStream
        ? "[AUDIO] playLegacyPanelAudioChunk bloqueado porque videoUsesStream=true"
        stopPanelAudio()
        return
    end if

    if not m.isFullscreen or m.audioSessionId = "" or m.audioChunkUrl = ""
        return
    end if

    audioItem = CreateObject("roAssociativeArray")
    audioItem.url = appendCacheBust(m.audioChunkUrl)
    audioItem.streamformat = "wav"
    audioItem.title = "Audio do painel"
    ? "[AUDIO] playLegacyPanelAudioChunk => session="; m.audioSessionId; " url="; audioItem.url

    if m.audioPlayer <> invalid
        m.audioPlayer.Stop()
    end if
    m.audioPlayer = CreateObject("roAudioPlayer")
    if m.audioPlayer = invalid or m.audioPlayer.SetLoop = invalid
        m.statusLabel.text = "Falha ao criar roAudioPlayer (thread errada?)"
        return
    end if
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
    stepSize = 5
    if m.heldDirectionKey <> ""
        if m.heldDirectionRepeatCount >= 8
            stepSize = 8
        else if m.heldDirectionRepeatCount >= 3
            stepSize = 6
        end if
    end if

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
    notifyInteractionOverlayPointerActivity()
end sub

sub startHeldDirection(key as string)
    stopHeldRemoteCommand()
    m.heldDirectionKey = key
    m.heldDirectionRepeatCount = 0
    ? "[CURSOR] hold-start => "; key
    moveCursor(key)
    if shouldCaptureInteractionOverlayPointerInput()
        ? "[OVERLAY] pointer => hover"
    end if
    dispatchPointerMove(true)

    if m.cursorMoveTimer <> invalid
        m.cursorMoveTimer.control = "stop"
        m.cursorMoveTimer.control = "start"
    end if
end sub

sub stopHeldDirection()
    if m.heldDirectionKey <> ""
        ? "[CURSOR] hold-stop => "; m.heldDirectionKey
    end if
    m.heldDirectionKey = ""
    m.heldDirectionRepeatCount = 0
    if m.cursorMoveTimer <> invalid and m.heldRemoteCommand = ""
        m.cursorMoveTimer.control = "stop"
    end if
end sub

sub startHeldRemoteCommand(key as string, command as string)
    stopHeldDirection()
    m.heldRemoteKey = key
    m.heldRemoteCommand = command
    if m.lastHeldRemoteDispatch = invalid
        m.lastHeldRemoteDispatch = CreateObject("roTimespan")
    end if
    m.lastHeldRemoteDispatch.Mark()
    if command = "overlay-seek-backward"
        ? "[OVERLAY] hold-seek-start => backward"
    else if command = "overlay-seek-forward"
        ? "[OVERLAY] hold-seek-start => forward"
    end if

    if command = "overlay-seek-backward"
        handleInteractionOverlayTransportShortcut("backward")
    else if command = "overlay-seek-forward"
        handleInteractionOverlayTransportShortcut("forward")
    else
        sendRemoteCommand(command)
    end if

    if m.cursorMoveTimer <> invalid
        m.cursorMoveTimer.control = "stop"
        m.cursorMoveTimer.control = "start"
    end if
end sub

sub stopHeldRemoteCommand()
    if m.heldRemoteCommand = "overlay-seek-backward"
        ? "[OVERLAY] hold-seek-stop => backward"
    else if m.heldRemoteCommand = "overlay-seek-forward"
        ? "[OVERLAY] hold-seek-stop => forward"
    end if
    m.heldRemoteKey = ""
    m.heldRemoteCommand = ""
    m.lastHeldRemoteDispatch = invalid
    if m.cursorMoveTimer <> invalid and m.heldDirectionKey = ""
        m.cursorMoveTimer.control = "stop"
    end if
end sub

sub onCursorMoveTimerFire()
    if not m.isFullscreen
        stopHeldDirection()
        stopHeldRemoteCommand()
        return
    end if

    if m.heldDirectionKey <> ""
        m.heldDirectionRepeatCount = m.heldDirectionRepeatCount + 1
        moveCursor(m.heldDirectionKey)
        if shouldCaptureInteractionOverlayPointerInput()
            ? "[OVERLAY] pointer => hover"
        end if
        dispatchPointerMove(false)
        return
    end if

    if m.heldRemoteCommand <> ""
        if m.lastHeldRemoteDispatch <> invalid and m.lastHeldRemoteDispatch.TotalMilliseconds() < m.heldRemoteDispatchIntervalMs
            return
        end if
        if m.lastHeldRemoteDispatch = invalid
            m.lastHeldRemoteDispatch = CreateObject("roTimespan")
        end if
        m.lastHeldRemoteDispatch.Mark()
        if m.heldRemoteCommand = "overlay-seek-backward"
            handleInteractionOverlayTransportShortcut("backward")
        else if m.heldRemoteCommand = "overlay-seek-forward"
            handleInteractionOverlayTransportShortcut("forward")
        else
            sendRemoteCommand(m.heldRemoteCommand)
        end if
        scheduleFullscreenRefresh()
    end if
end sub

sub dispatchPointerMove(force as boolean)
    if force
        if m.lastPointerMoveDispatch = invalid
            m.lastPointerMoveDispatch = CreateObject("roTimespan")
        end if
        m.lastPointerMoveDispatch.Mark()
        sendPointerCommand("move")
        return
    end if

    if m.lastPointerMoveDispatch = invalid
        m.lastPointerMoveDispatch = CreateObject("roTimespan")
        m.lastPointerMoveDispatch.Mark()
        sendPointerCommand("move")
        return
    end if

    if m.lastPointerMoveDispatch.TotalMilliseconds() < m.pointerMoveDispatchIntervalMs
        return
    end if

    m.lastPointerMoveDispatch.Mark()
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
    if m.fullscreenStreamingMode = "Video"
        reportVideoDiagHeartbeat()
    end if

    if m.fullscreenStreamingMode = "Video" and m.videoUsesStream and m.fullscreenVideoMode <> invalid
        videoState = LCase(getString(m.fullscreenVideoMode.state, ""))
        if videoState = "playing"
            return
        end if
    end if

    loadWindows(true)
    if m.fullscreenStreamingMode = "Interacao"
        if m.selectedIndex >= 0 and m.selectedIndex < m.windowEntries.Count()
            syncInteractionDirectVideoOverlay(m.windowEntries[m.selectedIndex], false)
        end if
        refreshFullscreenPreview()
    end if
end sub

sub onInteractionOverlayControlsTimerFire()
    if not isInteractionOverlayActive()
        hideInteractionOverlayControls()
        if m.interactionOverlayControlsTimer <> invalid
            m.interactionOverlayControlsTimer.control = "stop"
        end if
        return
    end if

    refreshInteractionOverlayControls()

    if m.interactionOverlayControlsLastActivity = invalid
        return
    end if

    state = LCase(getString(m.fullscreenInteractionOverlayVideo.state, ""))
    if state <> "playing"
        if not m.interactionOverlayControlsVisible
            showInteractionOverlayControls("state")
        end if
        return
    end if

    if m.interactionOverlayQualityMenuVisible
        showInteractionOverlayControls("quality-menu")
        return
    end if

    if m.interactionOverlayControlsLastActivity.TotalMilliseconds() >= m.interactionOverlayControlsHideDelayMs
        hideInteractionOverlayControls()
    end if
end sub

sub onFullscreenVideoStageStateChanged()
    if m.fullscreenVideoStage = invalid
        return
    end if

    if m.stagingVideoStreamUrl = ""
        if m.fullscreenVideoStage.state <> invalid and LCase(getString(m.fullscreenVideoStage.state, "")) <> ""
            m.fullscreenVideoStage.control = "stop"
            m.fullscreenVideoStage.content = invalid
            m.fullscreenVideoStage.visible = false
        end if
        return
    end if

    state = LCase(getString(m.fullscreenVideoStage.state, ""))
    if state = ""
        return
    end if

    ? "[HLS] stage video state => "; state

    if state = "playing"
        ? "[MODE] stage pronto, promovendo => "; m.stagingVideoTargetMode; " url="; m.stagingVideoStreamUrl
        notifyPendingModeSwitchReady(m.stagingVideoWindowId, m.stagingVideoTargetMode)
        if m.fullscreenVideoMode <> invalid
            content = CreateObject("roSGNode", "ContentNode")
            content.url = appendCacheBust(m.stagingVideoStreamUrl)
            content.streamFormat = "hls"
            content.title = "Painel"
            m.fullscreenVideoPendingRestart = true
            m.fullscreenVideoMode.content = content
            m.fullscreenVideoMode.control = "stop"
            m.fullscreenVideoMode.visible = true
            m.fullscreenVideoMode.control = "play"
        end if

        m.fullscreenVideoStage.control = "stop"
        m.fullscreenVideoStage.content = invalid
        m.fullscreenVideoStage.visible = false

        m.videoUsesStream = true
        m.videoStreamUrl = m.stagingVideoStreamUrl
        m.fullscreenAssignedStreamUrl = m.stagingVideoStreamUrl
        m.fullscreenStreamingMode = m.stagingVideoTargetMode
        m.fullscreenVideoLastPosition = -1
        m.fullscreenVideoStallCount = 0
        m.modeSwitchState = "idle"
        m.pendingModeSwitchWindowId = ""
        m.pendingModeSwitchCurrentMode = ""
        m.pendingModeSwitchTargetMode = ""
        m.pendingModeSwitchAckSent = false
        m.stagingVideoTargetMode = ""
        m.stagingVideoWindowId = ""
        m.stagingVideoStreamUrl = ""
        m.statusLabel.text = "Iniciando stream HLS do painel..."
        return
    end if

    if state = "error" or state = "finished" or state = "stopped"
        m.statusLabel.text = "Carregando modo Video..."
    else if state = "buffering"
        m.statusLabel.text = "Bufferizando modo Video..."
    end if
end sub

sub syncFullscreenStreamState(windowId as string)
    if not m.isFullscreen or windowId = ""
        return
    end if

    currentEntry = invalid
    for each entry in m.windowEntries
        if getString(entry.id, "") = windowId
            currentEntry = entry
            exit for
        end if
    end for

    if currentEntry = invalid
        return
    end if

    nextStreamingMode = normalizeStreamingMode(getString(currentEntry.streamingMode, "Interacao"))
    previousStreamingMode = normalizeStreamingMode(m.fullscreenStreamingMode)
    requestedStreamingMode = normalizeOptionalStreamingMode(getString(currentEntry.requestedStreamingMode, ""))
    modeSwitchPending = getBool(currentEntry.modeSwitchPending, false)
    nextStreamUrl = getString(currentEntry.streamUrl, "")
    nextUsesStream = Instr(1, LCase(nextStreamUrl), ".m3u8") > 0
    ? "[MODE] syncFullscreenStreamState => id="; windowId; " modo="; nextStreamingMode; " pendente="; modeSwitchPending; " alvo="; requestedStreamingMode; " nextStream="; nextStreamUrl

    if m.fullscreenVideo = invalid and m.fullscreenVideoMode = invalid
        return
    end if

    if nextStreamingMode = "Interacao"
        syncInteractionDirectVideoOverlay(currentEntry, false)
    else
        stopInteractionDirectVideoOverlay()
    end if

    if modeSwitchPending and requestedStreamingMode <> "" and requestedStreamingMode <> previousStreamingMode
        if m.pendingModeSwitchWindowId <> windowId or m.pendingModeSwitchTargetMode <> requestedStreamingMode
            ? "[MODE] interrompendo atual para troca => "; previousStreamingMode; " -> "; requestedStreamingMode
            teardownFullscreenPlayback("mode-switch-pending:" + previousStreamingMode + "->" + requestedStreamingMode)
            m.modeSwitchState = "waiting_ack"
            m.pendingModeSwitchWindowId = windowId
            m.pendingModeSwitchCurrentMode = previousStreamingMode
            m.pendingModeSwitchTargetMode = requestedStreamingMode
            m.pendingModeSwitchAckSent = false
            thumbnailUrl = getString(currentEntry.thumbnailUrl, "")
            if thumbnailUrl <> ""
                setFullscreenPoster(thumbnailUrl, windowId, true)
            end if
            m.statusLabel.text = "Trocando para modo " + requestedStreamingMode + "..."
        end if
        return
    end if

    if m.pendingModeSwitchWindowId = windowId
        if modeSwitchPending
            if m.modeSwitchState <> "waiting_ack"
                m.modeSwitchState = "waiting_ack"
            end if
            m.statusLabel.text = "Trocando para modo " + m.pendingModeSwitchTargetMode + "..."
            return
        end if

        if m.modeSwitchState = "waiting_ack"
            ? "[MODE] troca confirmada, aguardando stream => "; m.pendingModeSwitchCurrentMode; " -> "; nextStreamingMode
            m.modeSwitchState = "waiting_stream"
        end if

        if m.modeSwitchState = "waiting_stream"
            if not nextUsesStream
                ? "[MODE] aguardando stream do novo modo => "; nextStreamingMode; " url=<sem-stream>"
                m.statusLabel.text = "Carregando modo " + nextStreamingMode + "..."
                return
            end if

            if nextStreamingMode = "Video"
                ? "[MODE] stream do modo Video detectado, iniciando staging => "; nextStreamUrl
                startStagingFullscreenVideo(currentEntry, nextStreamUrl, nextStreamingMode)
                return
            end if

            ? "[MODE] stream pronto para troca => "; m.pendingModeSwitchCurrentMode; " -> "; nextStreamingMode; " url="; nextStreamUrl
        end if
    end if

    if previousStreamingMode <> nextStreamingMode
        ? "[MODE] transicao => "; previousStreamingMode; " -> "; nextStreamingMode
        if previousStreamingMode = "Interacao" and nextStreamingMode = "Video" and not nextUsesStream
            m.statusLabel.text = "Preparando modo Video..."
            return
        end if

        teardownFullscreenPlayback("mode-transition:" + previousStreamingMode + "->" + nextStreamingMode)
        m.fullscreenStreamingMode = nextStreamingMode
        thumbnailUrl = getString(currentEntry.thumbnailUrl, "")
        if thumbnailUrl <> ""
            setFullscreenPoster(thumbnailUrl, windowId, nextStreamingMode <> "Video")
        end if
        if nextStreamingMode = "Video" and not nextUsesStream
            m.statusLabel.text = "Aguardando stream HLS do painel..."
            return
        end if
    end if

    if not nextUsesStream
        if nextStreamingMode = "Video"
            stopInactiveModePlayback("Video", "video-without-stream")
            teardownFullscreenPlayback("video-without-stream")
            thumbnailUrl = getString(currentEntry.thumbnailUrl, "")
            if thumbnailUrl <> ""
                setFullscreenPoster(thumbnailUrl, windowId, false)
            end if
        end if
        return
    end if

    if nextStreamingMode = "Interacao"
        if nextStreamUrl = m.videoStreamUrl
            ? "[MODE] interacao mantendo stream atual => "; nextStreamUrl
            return
        end if

        ? "[MODE] iniciando pipeline Interacao => "; nextStreamUrl
        stopInactiveModePlayback("Interacao", "syncFullscreenStreamState")
        m.videoUsesStream = true
        m.videoStreamUrl = nextStreamUrl
        m.fullscreenStreamingMode = nextStreamingMode
        m.fullscreenVideoLastPosition = -1
        m.fullscreenVideoStallCount = 0

        content = CreateObject("roSGNode", "ContentNode")
        content.url = appendCacheBust(m.videoStreamUrl)
        content.streamFormat = "hls"
        content.title = getString(currentEntry.title, "Painel")
        m.fullscreenVideo.content = content
        m.fullscreenVideo.control = "stop"
        m.fullscreenVideo.visible = true
        m.fullscreenVideo.control = "play"
        notifyPendingModeSwitchReady(windowId, nextStreamingMode)
        m.statusLabel.text = "Recarregando stream do painel..."
        ? "[HLS] reload => "; content.url
        return
    end if

    if m.pendingModeSwitchWindowId = windowId and not m.videoUsesStream and previousStreamingMode = "Video"
        startStagingFullscreenVideo(currentEntry, nextStreamUrl, nextStreamingMode)
        return
    end if

    if nextStreamUrl = m.videoStreamUrl
        ? "[HLS] sync ignorado: mesmo stream => "; nextStreamUrl
        return
    end if

    ? "[MODE] iniciando pipeline Video => "; nextStreamUrl
    stopInactiveModePlayback("Video", "syncFullscreenStreamState")
    m.videoUsesStream = true
    m.videoStreamUrl = nextStreamUrl
    m.fullscreenStreamingMode = normalizeStreamingMode(getString(currentEntry.streamingMode, "Interacao"))
    m.fullscreenVideoPendingRestart = true
    m.fullscreenAssignedStreamUrl = nextStreamUrl
    m.fullscreenVideoLastPosition = -1
    m.fullscreenVideoStallCount = 0

    content = CreateObject("roSGNode", "ContentNode")
    content.url = appendCacheBust(m.videoStreamUrl)
    content.streamFormat = "hls"
    content.title = getString(currentEntry.title, "Painel")
    m.fullscreenPlayRequestCount = m.fullscreenPlayRequestCount + 1
    ? "[HLS] assign #"; m.fullscreenPlayRequestCount; " via sync => "; content.url
    if m.fullscreenVideoMode <> invalid
        m.fullscreenVideoMode.content = content
        m.fullscreenVideoMode.control = "stop"
        m.fullscreenVideoMode.visible = true
        m.fullscreenVideoMode.control = "play"
    end if
    m.statusLabel.text = "Recarregando stream do painel..."
    ? "[HLS] reload => "; content.url
end sub

sub setFullscreenPoster(thumbnailUrl as string, windowId as string, forceRefresh as boolean)
    if thumbnailUrl = invalid or thumbnailUrl = ""
        return
    end if

    resolvedUrl = thumbnailUrl
    if forceRefresh
        resolvedUrl = appendCacheBust(thumbnailUrl)
    else if m.fullscreenPosterWindowId = windowId and m.fullscreenPosterSourceUrl = thumbnailUrl
        m.activeFullscreenPoster.visible = true
        m.bufferFullscreenPoster.visible = false
        return
    end if

    m.activeFullscreenPoster.uri = resolvedUrl
    m.activeFullscreenPoster.visible = true
    m.bufferFullscreenPoster.visible = false
    m.bufferFullscreenPoster.uri = ""
    m.fullscreenPosterWindowId = windowId
    m.fullscreenPosterSourceUrl = thumbnailUrl
end sub

sub onClickControlTaskCompleted()
    if m.clickControlTask = invalid
        return
    end if

    if m.clickControlTask.responseBody = invalid or m.clickControlTask.responseBody = ""
        if isCurrentStreamingModeVideo()
            loadWindows(true)
        end if
        scheduleFullscreenRefresh()
        return
    end if

    result = ParseJson(m.clickControlTask.responseBody)
    if isCurrentStreamingModeVideo()
        loadWindows(true)
    end if
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

sub onControlTaskCompleted()
    if m.controlTask = invalid
        return
    end if

    if isCurrentStreamingModeVideo()
        loadWindows(true)
        scheduleFullscreenRefresh()
    end if
end sub

sub onTextControlTaskCompleted()
    closeKeyboardDialog()
    if isCurrentStreamingModeVideo()
        loadWindows(true)
    end if
    scheduleFullscreenRefresh()
end sub

function isCurrentStreamingModeVideo() as boolean
    return normalizeStreamingMode(m.fullscreenStreamingMode) = "Video"
end function

function normalizeStreamingMode(value as dynamic) as string
    normalized = LCase(getString(value, "Interacao"))
    if normalized = "video"
        return "Video"
    end if

    return "Interacao"
end function

function normalizeOptionalStreamingMode(value as dynamic) as string
    rawValue = getString(value, "")
    if rawValue = ""
        return ""
    end if

    return normalizeStreamingMode(rawValue)
end function

function resolveWindowEntryStreamingMode(window as object) as string
    streamUrl = LCase(getString(window.streamUrl, ""))
    if streamUrl <> ""
        if Instr(1, streamUrl, "/panel-roll/") > 0
            return "Video"
        end if

        if Instr(1, streamUrl, "/panel-interaction/") > 0
            return "Interacao"
        end if
    end if

    return normalizeStreamingMode(getString(window.streamingMode, "Interacao"))
end function

sub notifyModeSwitchApplied(windowId as string, previousMode as string, targetMode as string)
    if m.bridgeHost = invalid or m.bridgeHost = "" or m.deviceId = invalid or m.deviceId = "" or windowId = invalid or windowId = ""
        return
    end if

    if m.modeSwitchNotifyTask = invalid
        return
    end if

    ? "[MODE] enviando ACK de troca => "; previousMode; " -> "; targetMode; " janela="; windowId
    m.modeSwitchNotifyTask.bridgeHost = m.bridgeHost
    m.modeSwitchNotifyTask.deviceId = m.deviceId
    m.modeSwitchNotifyTask.windowId = windowId
    m.modeSwitchNotifyTask.previousMode = previousMode
    m.modeSwitchNotifyTask.targetMode = targetMode
    m.modeSwitchNotifyTask.control = "RUN"
end sub

sub notifyPendingModeSwitchReady(windowId as string, reachedMode as string)
    if m.pendingModeSwitchWindowId <> windowId
        return
    end if

    if m.pendingModeSwitchAckSent
        return
    end if

    targetMode = normalizeStreamingMode(m.pendingModeSwitchTargetMode)
    if targetMode = "" or targetMode <> normalizeStreamingMode(reachedMode)
        return
    end if

    m.pendingModeSwitchAckSent = true
    notifyModeSwitchApplied(windowId, m.pendingModeSwitchCurrentMode, targetMode)
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

    if normalizeStreamingMode(m.fullscreenStreamingMode) = "Interacao"
        m.fullscreenStreamTimer.duration = 0.20
    else
        m.fullscreenStreamTimer.duration = 0.75
    end if

    m.fullscreenStreamTimer.control = "stop"
    m.fullscreenStreamTimer.control = "start"
    if m.fullscreenVideoWatchTimer <> invalid
        m.fullscreenVideoWatchTimer.control = "stop"
        if m.videoUsesStream and normalizeStreamingMode(m.fullscreenStreamingMode) = "Video"
            m.fullscreenVideoWatchTimer.control = "start"
        end if
    end if
end sub

sub stopFullscreenStream()
    m.isFullscreenRefreshInFlight = false
    if m.fullscreenStreamTimer <> invalid
        m.fullscreenStreamTimer.control = "stop"
    end if
    if m.fullscreenVideoWatchTimer <> invalid
        m.fullscreenVideoWatchTimer.control = "stop"
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

function getNumber(value as dynamic, fallback as double) as double
    if value = invalid
        return fallback
    end if

    valueType = Type(value)
    if valueType = "Double" or valueType = "Float" or valueType = "roFloat"
        return value
    end if

    if valueType = "Integer" or valueType = "roInt" or valueType = "LongInteger"
        return value
    end if

    return fallback
end function

function getObjectArray(value as dynamic) as object
    if value = invalid
        return []
    end if

    valueType = Type(value)
    if valueType = "roArray" or valueType = "Array"
        return value
    end if

    return []
end function

function normalizeDirectVideoQualityOptions(options as dynamic, fallbackStreamUrl as string, fallbackStreamFormat as string, fallbackLabel as string) as object
    result = []
    for each option in getObjectArray(options)
        label = getString(option.label, "")
        streamUrl = getString(option.streamUrl, "")
        streamFormat = LCase(getString(option.streamFormat, ""))
        if label <> "" and streamUrl <> "" and streamFormat <> ""
            existingIndex = findDirectVideoQualityOptionIndex(result, "", label)
            if existingIndex >= 0 and existingIndex < result.Count()
                existingOption = result[existingIndex]
                existingFormat = LCase(getString(existingOption.streamFormat, ""))
                if existingFormat <> "hls" and streamFormat = "hls"
                    result[existingIndex] = {
                        label: label
                        streamUrl: streamUrl
                        streamFormat: streamFormat
                    }
                end if
            else
                result.Push({
                    label: label
                    streamUrl: streamUrl
                    streamFormat: streamFormat
                })
            end if
        end if
    end for

    if result.Count() = 0 and fallbackStreamUrl <> "" and fallbackStreamFormat <> ""
        fallbackResolvedLabel = fallbackLabel
        if fallbackResolvedLabel = ""
            fallbackResolvedLabel = "Auto"
        end if
        result.Push({
            label: fallbackResolvedLabel
            streamUrl: fallbackStreamUrl
            streamFormat: fallbackStreamFormat
        })
    end if

    for i = 0 to result.Count() - 2
        for j = i + 1 to result.Count() - 1
            leftHeight = getDirectVideoQualityHeight(getString(result[i].label, ""))
            rightHeight = getDirectVideoQualityHeight(getString(result[j].label, ""))
            if rightHeight > leftHeight
                temp = result[i]
                result[i] = result[j]
                result[j] = temp
            end if
        end for
    end for

    return result
end function

function findDirectVideoQualityOptionIndex(options as dynamic, streamUrl as string, qualityLabel as string) as integer
    objectOptions = getObjectArray(options)
    for i = 0 to objectOptions.Count() - 1
        option = objectOptions[i]
        if getString(option.streamUrl, "") = streamUrl
            return i
        end if
    end for

    for i = 0 to objectOptions.Count() - 1
        option = objectOptions[i]
        if getString(option.label, "") = qualityLabel
            return i
        end if
    end for

    return -1
end function

function getCurrentInteractionOverlayQualityLabel() as string
    if m.interactionOverlayAutoMode
        return "Auto"
    end if

    if m.interactionOverlayQualityOptions = invalid or m.interactionOverlayQualityOptions.Count() = 0
        return ""
    end if

    if m.interactionOverlaySelectedQualityIndex < 0 or m.interactionOverlaySelectedQualityIndex >= m.interactionOverlayQualityOptions.Count()
        return ""
    end if

    return getString(m.interactionOverlayQualityOptions[m.interactionOverlaySelectedQualityIndex].label, "")
end function

function getCurrentInteractionOverlayQualityButtonLabel() as string
    if m.interactionOverlayAutoMode
        autoLabel = getCurrentInteractionOverlayAutoResolvedLabel()
        if autoLabel <> ""
            return "Auto (" + autoLabel + ")"
        end if
        return "Auto"
    end if

    currentQuality = getCurrentInteractionOverlayQualityLabel()
    if currentQuality = ""
        autoLabel = getCurrentInteractionOverlayAutoResolvedLabel()
        if autoLabel <> ""
            return "Auto (" + autoLabel + ")"
        end if
        return "Auto"
    end if

    return currentQuality
end function

function getInteractionOverlayQualityMenuItemCount() as integer
    if m.interactionOverlayQualityOptions = invalid
        return 1
    end if

    optionCount = m.interactionOverlayQualityOptions.Count() + 1
    maxCount = m.interactionOverlayQualityMenuItemLabels.Count()
    if optionCount > maxCount
        optionCount = maxCount
    end if
    return optionCount
end function

function getInteractionOverlayQualityMenuLabel(menuIndex as integer) as string
    if menuIndex = 0
        autoLabel = getCurrentInteractionOverlayAutoResolvedLabel()
        if autoLabel <> ""
            return "Auto (" + autoLabel + ")"
        end if
        return "Auto"
    end if

    optionIndex = menuIndex - 1
    if m.interactionOverlayQualityOptions = invalid or optionIndex < 0 or optionIndex >= m.interactionOverlayQualityOptions.Count()
        return ""
    end if

    return getString(m.interactionOverlayQualityOptions[optionIndex].label, "")
end function

function getInteractionOverlayQualityMenuSelectedIndex() as integer
    if m.interactionOverlayAutoMode
        return 0
    end if

    indexValue = m.interactionOverlaySelectedQualityIndex + 1
    itemCount = getInteractionOverlayQualityMenuItemCount()
    if indexValue < 0
        indexValue = 0
    else if indexValue >= itemCount
        indexValue = itemCount - 1
    end if
    return indexValue
end function

function getInteractionOverlayHoveredQualityMenuIndex() as integer
    if not m.interactionOverlayQualityMenuVisible
        return -1
    end if

    for i = 0 to m.interactionOverlayQualityMenuRects.Count() - 1
        if isPointInsideRect(m.cursorX, m.cursorY, m.interactionOverlayQualityMenuRects[i])
            return i
        end if
    end for

    return -1
end function

function resolveInteractionOverlayAutoQualityIndex(options as dynamic) as integer
    objectOptions = getObjectArray(options)
    if objectOptions.Count() = 0
        return 0
    end if

    targetHeight = m.displayHeight
    if targetHeight >= 1080
        targetHeight = 1080
    else if targetHeight >= 720
        targetHeight = 720
    else if targetHeight >= 480
        targetHeight = 480
    end if

    baseIndex = -1
    for i = 0 to objectOptions.Count() - 1
        optionHeight = getDirectVideoQualityHeight(getString(objectOptions[i].label, ""))
        if optionHeight <= 0
            optionHeight = 99999
        end if
        if optionHeight <= targetHeight
            baseIndex = i
            exit for
        end if
    end for

    if baseIndex < 0
        if getDirectVideoQualityHeight(getString(objectOptions[0].label, "")) <= 0
            baseIndex = 0
        else
            baseIndex = objectOptions.Count() - 1
        end if
    end if

    resolvedIndex = baseIndex + m.interactionOverlayAutoDegradeLevel
    if resolvedIndex >= objectOptions.Count()
        resolvedIndex = objectOptions.Count() - 1
    end if
    if resolvedIndex < 0
        resolvedIndex = 0
    end if
    return resolvedIndex
end function

function getCurrentInteractionOverlayAutoResolvedLabel() as string
    objectOptions = getObjectArray(m.interactionOverlayQualityOptions)
    if objectOptions.Count() = 0
        return ""
    end if

    autoIndex = resolveInteractionOverlayAutoQualityIndex(objectOptions)
    if autoIndex < 0 or autoIndex >= objectOptions.Count()
        return ""
    end if

    return getString(objectOptions[autoIndex].label, "")
end function

function tryDegradeInteractionOverlayAutoQuality(reason as string) as boolean
    if not m.interactionOverlayAutoMode or m.interactionOverlayQualityOptions = invalid or m.interactionOverlayQualityOptions.Count() <= 1
        return false
    end if

    currentAutoIndex = resolveInteractionOverlayAutoQualityIndex(m.interactionOverlayQualityOptions)
    if currentAutoIndex >= m.interactionOverlayQualityOptions.Count() - 1
        return false
    end if

    m.interactionOverlayAutoDegradeLevel = m.interactionOverlayAutoDegradeLevel + 1
    nextAutoIndex = resolveInteractionOverlayAutoQualityIndex(m.interactionOverlayQualityOptions)
    if nextAutoIndex = currentAutoIndex
        return false
    end if

    ? "[OVERLAY] auto-quality => degrade level="; m.interactionOverlayAutoDegradeLevel; " reason="; reason
    applyInteractionOverlayQualityOption(nextAutoIndex, "auto-" + reason)
    return true
end function

function getDirectVideoQualityHeight(label as string) as integer
    safeLabel = LCase(getString(label, ""))
    numberText = ""
    for i = 1 to Len(safeLabel)
        ch = Mid(safeLabel, i, 1)
        if ch >= "0" and ch <= "9"
            numberText = numberText + ch
        else if ch = "p" and numberText <> ""
            return Val(numberText)
        else if numberText <> ""
            exit for
        end if
    end for

    if numberText <> ""
        return Val(numberText)
    end if

    return 0
end function

function clampNumber(value as double, minValue as double, maxValue as double) as double
    if value < minValue
        return minValue
    end if

    if value > maxValue
        return maxValue
    end if

    return value
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
    return key = "volumeup" or key = "channelup" or key = "plus" or key = "+"
end function

function isMinusKey(key as string) as boolean
    return key = "volumedown" or key = "channeldown" or key = "minus" or key = "-"
end function

function isOptionsKey(key as string) as boolean
    return key = "info" or key = "options"
end function

function isBlueKey(key as string) as boolean
    return key = "blue" or key = "bluekey" or key = "functionblue"
end function

sub showScrollModeToast(message as string)
    if m.fullscreenToastLabel <> invalid and m.isFullscreen
        m.fullscreenToastLabel.text = message
        if m.fullscreenToastGroup <> invalid
            m.fullscreenToastGroup.visible = true
        end if
    else if m.statusLabel <> invalid
        m.statusLabel.text = message
        m.statusLabel.visible = true
    else
        return
    end if

    if m.scrollModeToastTimer <> invalid
        m.scrollModeToastTimer.control = "stop"
        m.scrollModeToastTimer.control = "start"
    end if
end sub

sub onScrollModeToastTimerFire()
    if m.isFullscreen
        if m.fullscreenToastGroup <> invalid
            m.fullscreenToastGroup.visible = false
        end if
        if m.fullscreenToastLabel <> invalid
            m.fullscreenToastLabel.text = ""
        end if
        return
    end if

    refreshGrid()
end sub
