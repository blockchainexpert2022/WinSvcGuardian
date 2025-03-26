using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;

class Program
{
    private static readonly string configFilePath = "services.txt";
    private static readonly string[] defaultServices =
    {
        "Spooler", "wuauserv", "SSDPSRV", "SENS", "Lanmanserver", "hmpalertsvc", "WdiSystemHost", "IntelAudioService", "cplspcon",
        "DPS", "CryptSvc", "Sophos Endpoint Defense Service", "Sophos File Scanner Service", "Sophos Health Service", "Sophos MCS Agent",
        "Sophos MCS Client", "SntpService",
    };

    private static readonly object fileLock = new object();
    private static readonly TimeSpan statusCheckInterval = TimeSpan.FromSeconds(5);
    private static Dictionary<string, ServiceControllerStatus> lastKnownStatuses = new Dictionary<string, ServiceControllerStatus>();

    static async Task Main()
    {
        InitializeConfigFile();
        InitializeStatusTracking();
        await RunServiceMonitor();
    }

    private static void InitializeConfigFile()
    {
        Console.WriteLine("Initialisation du fichier de configuration...");
        
        lock (fileLock)
        {
            if (!File.Exists(configFilePath))
            {
                Console.WriteLine("Création du fichier avec les services par défaut.");
                File.WriteAllLines(configFilePath, defaultServices);
            }
            else
            {
                var existingServices = ReadConfigFile();
                var newServices = defaultServices.Except(existingServices, StringComparer.OrdinalIgnoreCase).ToList();
                
                if (newServices.Any())
                {
                    Console.WriteLine($"Ajout de {newServices.Count} services par défaut manquants.");
                    AppendToConfigFile(newServices);
                }
            }
        }
    }

    private static void InitializeStatusTracking()
    {
        foreach (var service in ServiceController.GetServices())
        {
            lastKnownStatuses[service.ServiceName] = service.Status;
        }
    }

    private static async Task RunServiceMonitor()
    {
        Console.WriteLine("Démarrage du moniteur de services...");

        while (true)
        {
            try
            {
                var servicesToMonitor = ReadConfigFile();
                var allServices = ServiceController.GetServices();

                foreach (var service in allServices)
                {
                    // Vérifier les changements d'état
                    if (lastKnownStatuses.TryGetValue(service.ServiceName, out var lastStatus))
                    {
                        if (lastStatus == ServiceControllerStatus.Running && 
                            service.Status != ServiceControllerStatus.Running)
                        {
                            // Service vient d'être arrêté
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service arrêté détecté: {service.ServiceName}");
                            AddServiceToConfigIfMissing(service.ServiceName);
                        }
                    }
                    lastKnownStatuses[service.ServiceName] = service.Status;

                    // Gérer l'arrêt des services surveillés
                    if (service.Status == ServiceControllerStatus.Running && 
                        servicesToMonitor.Contains(service.ServiceName, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arrêt du service surveillé: {service.ServiceName}");
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Échec de l'arrêt: {service.ServiceName} - {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Erreur du moniteur: {ex.Message}");
            }

            await Task.Delay(statusCheckInterval);
        }
    }

    private static void AddServiceToConfigIfMissing(string serviceName)
    {
        lock (fileLock)
        {
            var existingServices = ReadConfigFile();
            if (!existingServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ajout du nouveau service arrêté: {serviceName}");
                AppendToConfigFile(new[] { serviceName });
            }
        }
    }

    private static List<string> ReadConfigFile()
    {
        return File.ReadAllLines(configFilePath)
                  .Select(line => line.Trim())
                  .Where(line => !string.IsNullOrWhiteSpace(line))
                  .ToList();
    }

    private static void AppendToConfigFile(IEnumerable<string> services)
    {
        File.AppendAllLines(configFilePath, services);
    }
}