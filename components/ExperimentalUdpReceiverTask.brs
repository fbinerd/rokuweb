sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    socket = CreateObject("roDatagramSocket")
    if socket = invalid
        m.top.errorMessage = "roDatagramSocket indisponivel."
        m.top.completedToken = "error-socket"
        return
    end if

    port = CreateObject("roMessagePort")
    socket.setPort(port)

    ok = socket.bindToLocalPort(0)
    if not ok
        reason = ""
        if GetInterface(socket, "ifDatagramSocket") <> invalid
            reason = getString(socket.getFailureReason(), "")
        end if
        if reason = ""
            reason = "Falha ao bindar porta UDP local."
        end if
        m.top.errorMessage = reason
        m.top.completedToken = "error-bind"
        return
    end if

    m.top.receiverPort = socket.getLocalPort()
    m.top.completedToken = "ready-" + m.top.receiverPort.ToStr()

    while true
        message = wait(1000, port)
        if Type(message) = "roDatagramEvent"
            byteArray = message.getByteArray()
            bytesReceived = 0
            if byteArray <> invalid
                bytesReceived = byteArray.Count()
            end if

            m.top.packetCount = m.top.packetCount + 1
            m.top.byteCount = m.top.byteCount + bytesReceived
            m.top.lastSourceHost = getString(message.getSourceHost(), "")
            m.top.completedToken = "packet-" + m.top.packetCount.ToStr()
        end if
    end while
end sub

function getString(value as dynamic, fallback as string) as string
    if value = invalid
        return fallback
    end if

    valueType = Type(value)
    if valueType = "roString" or valueType = "String"
        return value
    end if

    return fallback
end function
