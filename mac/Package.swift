// swift-tools-version:5.9
// ПРОТОТИП macOS-бэкенда. Не собирался и не проверялся (в среде разработки нет macOS). См. mac/README.md.
import PackageDescription

let package = Package(
    name: "NumPadMac",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "NumPadMac",
            path: "Sources/NumPadMac",
            linkerSettings: [
                .linkedFramework("ApplicationServices"),
                .linkedFramework("Carbon"),
            ]
        )
    ]
)
