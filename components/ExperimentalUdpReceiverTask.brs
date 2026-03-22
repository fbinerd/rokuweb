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
        sleep(1000)
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
