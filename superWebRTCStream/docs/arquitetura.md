# Arquitetura Proposta

## Objetivo

Construir um gerenciador de janelas para Windows capaz de:

- instanciar navegadores embarcados independentes
- exibir preview e controle de cada instancia em um painel central
- associar cada janela a um destino de exibicao diferente
- transmitir o conteudo de cada janela para TVs ou receptores distintos

## Componentes principais

### 1. Window Manager

Responsavel por:

- criar e destruir janelas
- manter o estado de cada instancia
- controlar layout, foco e roteamento

### 2. Browser Host

Camada que encapsula o navegador embarcado.

Responsabilidades:

- abrir URL inicial
- controlar navegacao
- expor handle da janela para captura
- isolar sessoes por instancia

Implementacoes candidatas:

- `CefSharp`
- `CEF nativo + wrapper proprio`

### 3. Preview Surface

Responsavel por mostrar no gerenciador uma visualizacao de cada janela remota/local e repassar interacoes do operador.

Responsabilidades:

- desenhar thumbnails ou preview em tempo real
- encaminhar clique, teclado e foco
- sinalizar estado de conexao e transmissao

### 4. Display Discovery

Descobre destinos de exibicao.

Responsabilidades:

- localizar receptores disponiveis
- manter status online/offline
- informar tipo de transporte suportado

Tipos de destino:

- receptor Miracast
- smart TV com app receptor
- mini PC ou player conectado a TV
- set-top box proprietario

### 5. Capture Pipeline

Captura o conteudo de uma janela individual.

Responsabilidades:

- capturar apenas a janela associada
- converter frame para formato de encoder
- gerenciar taxa de quadros e resolucao

Tecnologias possiveis:

- `Windows Graphics Capture`
- `Desktop Duplication API`

### 6. Display Transport

Camada de envio do stream.

Subtipos previstos:

- `MiracastTransport`
- `LanStreamingTransport`

Responsabilidades:

- conectar no destino
- iniciar/parar stream
- reportar latencia, bitrate e erros

## Decisao tecnica importante

Se o requisito central for "uma janela por TV em rede cabeada", o caminho mais robusto e tratar Miracast apenas como opcional.

Motivo:

- Miracast nao foi desenhado como barramento generico de distribuicao para varios destinos cabeados
- o controle fino por janela e destino fica melhor com um protocolo proprio sobre LAN

## Arquitetura logica

```text
Operator UI
   |
WindowCoordinator
   |-- BrowserInstanceManager
   |-- PreviewManager
   |-- DisplayDiscoveryService
   |-- RoutingService
          |-- CaptureSessionFactory
          |-- DisplayTransportResolver
                    |-- MiracastTransport
                    |-- LanStreamingTransport
```

## Modelo operacional

### Criacao de janela

1. Operador solicita nova instancia.
2. `BrowserInstanceManager` cria a janela do navegador.
3. A instancia recebe um `WindowSessionId`.
4. O preview e registrado na UI.

### Publicacao para TV

1. Operador escolhe a janela.
2. Operador seleciona uma TV/receptor.
3. `RoutingService` verifica compatibilidade.
4. `CaptureSessionFactory` inicia captura da janela.
5. `DisplayTransport` escolhido publica o stream.

### Interacao pelo gerenciador

1. Preview recebe clique ou teclado.
2. Evento e traduzido para coordenadas/foco da janela original.
3. A instancia do navegador processa a entrada.

## Riscos principais

### Miracast

- suporte varia por hardware e driver
- geralmente nao atende bem multiplos destinos independentes
- nao e a melhor opcao para infraestrutura cabeada

### CEF

- maior complexidade de empacotamento
- consumo de memoria relevante com multiplas instancias

### Captura por janela

- restricoes de conteudo protegido
- necessidade de sincronizacao entre captura, preview e transmissao

## Recomendacao de MVP

### Fase 1

- gerenciador de janelas
- multiplas instancias de navegador
- preview local e controle de foco

### Fase 2

- descoberta de destinos
- associacao janela -> destino
- streaming via LAN para um receptor proprio

### Fase 3

- suporte experimental a Miracast para casos compativeis
