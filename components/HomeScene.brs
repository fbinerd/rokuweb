sub init()
    m.bridgeHost = "10.1.0.10:8090"
    m.windowEntries = []
    m.selectedIndex = 0
    m.pageStart = 0
    m.pageSize = 6
    m.gridColumns = 3
    m.isFullscreen = false
    m.cursorX = 640
    m.cursorY = 360

    m.titleLabel = m.top.findNode("titleLabel")
    m.statusLabel = m.top.findNode("statusLabel")
    m.subtitleLabel = m.top.findNode("subtitleLabel")
    m.fullscreenPoster = m.top.findNode("fullscreenPoster")
    m.cursorMarker = m.top.findNode("cursorMarker")
    m.bridgeRequestTask = m.top.findNode("bridgeRequestTask")
    m.inputLogTask = m.top.findNode("inputLogTask")
    m.controlTask = m.top.findNode("controlTask")

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
    m.top.setFocus(true)

    m.statusLabel.text = "Canal iniciado"
    m.subtitleLabel.text = "Pressione OK para consultar o servidor"
    hideGrid()
end sub

function onKeyEvent(key as string, press as boolean) as boolean
    if not press
        return false
    end if

    reportInputKey(key)

    if m.isFullscreen
        if key = "back" or key = "Back"
            hideFullscreen()
            return true
        end if

        if key = "OK"
            sendPointerCommand("click")
            refreshFullscreenPreview()
            return true
        end if

        if key = "up" or key = "down" or key = "left" or key = "right"
            moveCursor(key)
            sendPointerCommand("move")
            refreshFullscreenPreview()
            return true
        end if

        if key = "Play"
            sendRemoteCommand("tab")
            refreshFullscreenPreview()
            return true
        end if

        return false
    end if

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
            loadWindows()
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
    m.inputLogTask.control = "RUN"
end sub

sub loadWindows()
    m.statusLabel.text = "Consultando bridge em " + m.bridgeHost
    m.subtitleLabel.text = "Aguarde a resposta do servidor"
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
        m.statusLabel.text = "Falha ao conectar em " + m.bridgeHost
        m.subtitleLabel.text = "Verifique se o app .NET esta aberto na mesma rede"
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

    m.windowEntries = []
    m.selectedIndex = 0
    m.pageStart = 0

    for each window in json.windows
        m.windowEntries.Push({
            id: getString(window.id, "")
            title: getString(window.title, "Janela sem titulo")
            state: getString(window.state, "Desconhecido")
            thumbnailUrl: getString(window.thumbnailUrl, "")
            initialUrl: getString(window.initialUrl, "")
        })
    end for

    if m.windowEntries.Count() = 0
        m.statusLabel.text = "Bridge conectado, sem paineis disponiveis"
        m.subtitleLabel.text = "Nenhuma janela publicada no app .NET ainda."
        hideGrid()
        return
    end if

    m.statusLabel.text = "Bridge conectado em " + m.bridgeHost
    refreshGrid()
    m.top.setFocus(true)
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
    m.fullscreenPoster.visible = true
    m.fullscreenPoster.uri = appendCacheBust(entry.thumbnailUrl)
    m.cursorMarker.visible = true
    updateCursorMarker()
    m.top.setFocus(true)
end sub

sub hideFullscreen()
    m.isFullscreen = false
    m.fullscreenPoster.visible = false
    m.cursorMarker.visible = false
    m.titleLabel.visible = true
    m.statusLabel.visible = true
    m.subtitleLabel.visible = true
    refreshGrid()
    m.top.setFocus(true)
end sub

sub moveCursor(command as string)
    stepSize = 48

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

sub updateCursorMarker()
    m.cursorMarker.translation = [m.cursorX - 13, m.cursorY - 13]
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
    m.controlTask.control = "RUN"
end sub

sub refreshFullscreenPreview()
    if not m.isFullscreen or m.windowEntries.Count() = 0
        return
    end if

    entry = m.windowEntries[m.selectedIndex]
    if entry.thumbnailUrl <> invalid and entry.thumbnailUrl <> ""
        m.fullscreenPoster.uri = appendCacheBust(entry.thumbnailUrl)
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

    now = CreateObject("roDateTime")
    return url + separator + "ts=" + now.AsSeconds().ToStr()
end function

function trimTitle(title as string) as string
    if Len(title) <= 34
        return title
    end if

    return Left(title, 31) + "..."
end function
