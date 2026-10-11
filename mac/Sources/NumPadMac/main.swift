// NumPad from TouchPad — прототип бэкенда для macOS (MacBook).
//
// СТАТУС: не собирался и не запускался (нет macOS в среде разработки). Это отправная точка для портирования,
// а не готовая поддержка macOS. Логика повторяет NumPad.Core: та же JSON-раскладка, та же калибровка
// (аффинное преобразование), тот же автомат «касание → зона → KeyDown/KeyUp» с подсчётом ссылок.
//
// Ввод:   приватный MultitouchSupport.framework (dlopen) — нормализованные координаты пальцев 0..1 в фоне.
// Вывод:  CGEventPost с keypad-кодами (нужно разрешение Accessibility).
// Хоткей: Carbon RegisterEventHotKey — Ctrl+Option+N.
// Подавление курсора: CGEventTap на события мыши, пока палец на зоне клавиш.
//
// Запуск: swift run NumPadMac [путь к раскладке.json]   (по умолчанию ~/Library/Application Support/NumPadFromTouchPad/layout.json)

import Foundation
import ApplicationServices
import Carbon.HIToolbox

// MARK: - Модель (совместима с JSON NumPad.Core)

struct KeyAction: Codable { var Kind: String; var Key: String?; var Modifiers: [String]?; var Text: String? }
struct KeyZone: Codable { var Id: String; var Label: String; var X: Double; var Y: Double; var Width: Double; var Height: Double; var Action: KeyAction; var Enabled: Bool? }
struct Layout: Codable { var Id: String; var Name: String; var Keys: [KeyZone] }
struct Affine { var a = 1.0, b = 0.0, c = 0.0, d = 0.0, e = 1.0, f = 0.0
    func apply(_ x: Double, _ y: Double) -> (Double, Double) { (a * x + b * y + c, d * x + e * y + f) } }

// Таблица keycode (Carbon kVK_*). Имена — как в KeyCatalog.cs.
let keyCodes: [String: CGKeyCode] = [
    "Numpad0": 0x52, "Numpad1": 0x53, "Numpad2": 0x54, "Numpad3": 0x55, "Numpad4": 0x56,
    "Numpad5": 0x57, "Numpad6": 0x58, "Numpad7": 0x59, "Numpad8": 0x5B, "Numpad9": 0x5C,
    "NumpadDecimal": 0x41, "NumpadAdd": 0x45, "NumpadSubtract": 0x4E, "NumpadMultiply": 0x43,
    "NumpadDivide": 0x4B, "NumpadEnter": 0x4C, "NumLock": 0x47 /* Clear */,
    "Enter": 0x24, "Tab": 0x30, "Escape": 0x35, "Backspace": 0x33, "Delete": 0x75, "Space": 0x31,
    "Home": 0x73, "End": 0x77, "PageUp": 0x74, "PageDown": 0x79,
    "Up": 0x7E, "Down": 0x7D, "Left": 0x7B, "Right": 0x7C,
    "LeftShift": 0x38, "LeftCtrl": 0x3B, "LeftAlt": 0x3A, "LeftWin": 0x37,
    "A": 0x00, "C": 0x08, "V": 0x09, "X": 0x07, "Z": 0x06,
]
let modifierAlias = ["Shift": "LeftShift", "Ctrl": "LeftCtrl", "Alt": "LeftAlt", "Win": "LeftWin"]

// MARK: - Вывод

func post(_ name: String, down: Bool) {
    let n = modifierAlias[name] ?? name
    guard let code = keyCodes[n], let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down) else { return }
    ev.post(tap: .cghidEventTap)
}

func typeText(_ s: String) {
    let utf16 = Array(s.utf16)
    for down in [true, false] {
        guard let ev = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: down) else { continue }
        ev.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: utf16)
        ev.post(tap: .cghidEventTap)
    }
}

// MARK: - Автомат нажатий (упрощённый порт KeyPressEngine)

final class Engine {
    var layout: Layout
    var transform = Affine()
    var contacts: [Int32: [String]] = [:]   // id касания → удерживаемые клавиши
    var refCount: [String: Int] = [:]
    var lastActivity = Date.distantPast

    init(layout: Layout) { self.layout = layout }

    func hit(_ x: Double, _ y: Double) -> KeyZone? {
        let zs = layout.Keys.filter { ($0.Enabled ?? true) && x >= $0.X && x <= $0.X + $0.Width && y >= $0.Y && y <= $0.Y + $0.Height }
        return zs.min { hypot($0.X + $0.Width / 2 - x, $0.Y + $0.Height / 2 - y) < hypot($1.X + $1.Width / 2 - x, $1.Y + $1.Height / 2 - y) }
    }

    func acquire(_ k: String) { let c = refCount[k, default: 0]; if c == 0 { post(k, down: true) }; refCount[k] = c + 1 }
    func release(_ k: String) { let c = refCount[k, default: 0]; guard c > 0 else { return }
        if c == 1 { refCount[k] = nil; post(k, down: false) } else { refCount[k] = c - 1 } }

    func frame(_ touches: [(id: Int32, x: Double, y: Double, tip: Bool)]) {
        lastActivity = Date()
        var seen = Set<Int32>()
        for t in touches {
            seen.insert(t.id)
            if contacts[t.id] != nil { if !t.tip { end(t.id) }; continue }
            guard t.tip else { continue }
            let (ax, ay) = transform.apply(t.x, t.y)
            var held: [String] = []
            if let z = hit(ax, ay) {
                switch z.Action.Kind {
                case "Key": if let k = z.Action.Key { acquire(k); held = [k] }
                case "Combo": if let k = z.Action.Key { held = (z.Action.Modifiers ?? []) + [k]; held.forEach(acquire) }
                case "Text": if let s = z.Action.Text { typeText(s) }
                default: break
                }
            }
            contacts[t.id] = held
        }
        for id in contacts.keys where !seen.contains(id) { end(id) } // кадр MultitouchSupport всегда полный
    }

    func end(_ id: Int32) { (contacts.removeValue(forKey: id) ?? []).reversed().forEach(release) }
    func releaseAll() { for id in Array(contacts.keys) { end(id) }; for k in Array(refCount.keys) { refCount[k] = nil; post(k, down: false) } }
}

// MARK: - MultitouchSupport (приватный фреймворк)

struct MTPoint { var x: Float; var y: Float }
struct MTVector { var position: MTPoint; var velocity: MTPoint }
struct MTTouch {
    var frame: Int32; var timestamp: Double; var identifier: Int32; var state: Int32
    var fingerId: Int32; var handId: Int32; var normalized: MTVector; var size: Float; var zero1: Int32
    var angle: Float; var majorAxis: Float; var minorAxis: Float; var mm: MTVector; var zero2: (Int32, Int32); var unk2: Float
}
typealias MTCallback = @convention(c) (Int32, UnsafeMutablePointer<MTTouch>?, Int32, Double, Int32) -> Int32
typealias MTDeviceCreateListFn = @convention(c) () -> Unmanaged<CFMutableArray>
typealias MTRegisterFn = @convention(c) (UnsafeMutableRawPointer, MTCallback) -> Void
typealias MTStartFn = @convention(c) (UnsafeMutableRawPointer, Int32) -> Void

var engine: Engine!
var numPadOn = false

let callback: MTCallback = { _, data, count, _, _ in
    guard numPadOn, let data else { return 0 }
    var touches: [(id: Int32, x: Double, y: Double, tip: Bool)] = []
    for i in 0..<Int(count) {
        let t = data[i]
        // state 3 = MakeTouch, 4 = Touching; y у MultitouchSupport растёт вверх — переворачиваем.
        touches.append((t.identifier, Double(t.normalized.position.x), 1 - Double(t.normalized.position.y), t.state == 3 || t.state == 4))
    }
    DispatchQueue.main.async { engine.frame(touches) }
    return 0
}

func startMultitouch() -> Bool {
    guard let h = dlopen("/System/Library/PrivateFrameworks/MultitouchSupport.framework/MultitouchSupport", RTLD_NOW),
          let pList = dlsym(h, "MTDeviceCreateList"), let pReg = dlsym(h, "MTRegisterContactFrameCallback"), let pStart = dlsym(h, "MTDeviceStart")
    else { return false }
    let createList = unsafeBitCast(pList, to: MTDeviceCreateListFn.self)
    let register = unsafeBitCast(pReg, to: MTRegisterFn.self)
    let start = unsafeBitCast(pStart, to: MTStartFn.self)
    let list = createList().takeRetainedValue() as NSArray
    for dev in list {
        let ptr = Unmanaged.passUnretained(dev as AnyObject).toOpaque()
        register(ptr, callback)
        start(ptr, 0)
    }
    return list.count > 0
}

// MARK: - Подавление курсора (CGEventTap)

func installMouseTap() {
    let mask = (1 << CGEventType.mouseMoved.rawValue) | (1 << CGEventType.leftMouseDown.rawValue) | (1 << CGEventType.leftMouseUp.rawValue)
        | (1 << CGEventType.leftMouseDragged.rawValue) | (1 << CGEventType.scrollWheel.rawValue) | (1 << CGEventType.rightMouseDown.rawValue)
    guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
        eventsOfInterest: CGEventMask(mask), callback: { _, _, event, _ in
            if numPadOn && (!engine.contacts.isEmpty || Date().timeIntervalSince(engine.lastActivity) < 0.35) { return nil }
            return Unmanaged.passUnretained(event)
        }, userInfo: nil) else { print("CGEventTap недоступен: нужно разрешение Accessibility"); return }
    let src = CFMachPortCreateRunLoopSource(nil, tap, 0)
    CFRunLoopAddSource(CFRunLoopGetMain(), src, .commonModes)
    CGEvent.tapEnable(tap: tap, enable: true)
}

// MARK: - Хоткей (Ctrl+Option+N)

func installHotkey() {
    var ref: EventHotKeyRef?
    let id = EventHotKeyID(signature: OSType(0x4E505450), id: 1)
    RegisterEventHotKey(UInt32(kVK_ANSI_N), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &ref)
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
        numPadOn.toggle()
        if !numPadOn { engine.releaseAll() }
        print(numPadOn ? "NumPad: ВКЛ" : "NumPad: ВЫКЛ")
        return noErr
    }, 1, &spec, nil, nil)
}

// MARK: - main

let path = CommandLine.arguments.dropFirst().first
    ?? (NSHomeDirectory() + "/Library/Application Support/NumPadFromTouchPad/layout.json")
guard let data = FileManager.default.contents(atPath: path), let layout = try? JSONDecoder().decode(Layout.self, from: data) else {
    print("Не удалось прочитать раскладку: \(path). Экспортируйте её из Windows-версии (Раскладки → Экспорт).")
    exit(1)
}
engine = Engine(layout: layout)
if !AXIsProcessTrusted() { print("Нужно разрешение: Системные настройки → Конфиденциальность → Универсальный доступ.") }
guard startMultitouch() else { print("MultitouchSupport недоступен или трекпад не найден."); exit(2) }
installMouseTap()
installHotkey()
signal(SIGINT) { _ in engine.releaseAll(); exit(0) }
print("Раскладка «\(layout.Name)». Ctrl+Option+N — переключить режим.")
RunApplicationEventLoop()
