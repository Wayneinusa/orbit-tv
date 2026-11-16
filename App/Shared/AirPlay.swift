import SwiftUI
import UIKit

// AirPlay for the iPad.
//
// The player uses the VLC engine, because Apple's own player cannot play most IPTV streams. Apple only
// hands a video over to an Apple TV ("AirPlay video") from its own player, so that route is not open to
// this app. What does work for every app is Screen Mirroring, and an app may then draw something else on
// the TV than on the iPad. That is what happens here: while the iPad is mirrored, the picture is drawn on
// the TV at the TV's full size and the iPad keeps the controls.

#if os(iOS)

/// The TV (or any other external screen) the iPad is connected to.
@MainActor
final class ExternalDisplay: ObservableObject {
    static let shared = ExternalDisplay()

    /// True while the picture is being drawn on an external screen.
    @Published private(set) var isConnected = false

    private var window: UIWindow?
    private let host = SurfaceHostView()
    private let idleLabel = UILabel()

    /// Settings › AirPlay. When off, the TV simply shows a copy of the iPad's screen.
    static var isEnabled: Bool {
        UserDefaults.standard.object(forKey: "airPlayFullScreen") as? Bool ?? true
    }

    func connect(scene: UIWindowScene) {
        let controller = UIViewController()
        controller.view.backgroundColor = .black

        idleLabel.text = "Superior IPTV Player\nChoose something to watch on the iPad"
        idleLabel.numberOfLines = 0
        idleLabel.textAlignment = .center
        idleLabel.textColor = UIColor(white: 1, alpha: 0.45)
        idleLabel.font = .systemFont(ofSize: 34, weight: .semibold)
        idleLabel.frame = controller.view.bounds
        idleLabel.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        controller.view.addSubview(idleLabel)

        host.backgroundColor = .black
        host.clipsToBounds = true
        host.isHidden = true
        host.frame = controller.view.bounds
        host.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        controller.view.addSubview(host)

        let window = UIWindow(windowScene: scene)
        window.rootViewController = controller
        window.isHidden = false
        self.window = window
        isConnected = true
    }

    func disconnect() {
        window?.isHidden = true
        window = nil
        isConnected = false
    }

    /// Puts a player's picture on the TV.
    func show(_ video: UIView) {
        guard isConnected else { return }
        host.host(video)
    }

    /// Shows the picture (something is playing) or the "choose something" text (nothing is).
    func setPlaying(_ playing: Bool) {
        host.isHidden = !playing
    }
}

/// Tells iOS who looks after each screen: SwiftUI the iPad's own, ExternalSceneDelegate a connected TV.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication, configurationForConnecting connectingSceneSession: UISceneSession,
                     options: UIScene.ConnectionOptions) -> UISceneConfiguration {
        let configuration = UISceneConfiguration(name: nil, sessionRole: connectingSceneSession.role)
        // Without a delegate nothing is drawn on the TV and iOS mirrors the iPad, as it always did.
        if connectingSceneSession.role == .windowExternalDisplayNonInteractive && ExternalDisplay.isEnabled {
            configuration.delegateClass = ExternalSceneDelegate.self
        }
        return configuration
    }
}

final class ExternalSceneDelegate: UIResponder, UIWindowSceneDelegate {
    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }
        ExternalDisplay.shared.connect(scene: windowScene)
    }

    func sceneDidDisconnect(_ scene: UIScene) {
        ExternalDisplay.shared.disconnect()
    }
}

#else

/// Apple TV has no second screen; this stand-in lets the shared screens ask the same questions.
@MainActor
final class ExternalDisplay: ObservableObject {
    static let shared = ExternalDisplay()
    @Published private(set) var isConnected = false
    func show(_ video: UIView) {}
    func setPlaying(_ playing: Bool) {}
}

#endif
