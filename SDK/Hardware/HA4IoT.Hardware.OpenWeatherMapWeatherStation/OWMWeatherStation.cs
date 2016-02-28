using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using HA4IoT.Contracts;
using HA4IoT.Contracts.Actuators;
using HA4IoT.Contracts.Core;
using HA4IoT.Contracts.Hardware;
using HA4IoT.Contracts.Logging;
using HA4IoT.Contracts.Networking;
using HA4IoT.Contracts.WeatherStation;
using HA4IoT.Networking;
using HttpStatusCode = HA4IoT.Contracts.Networking.HttpStatusCode;

namespace HA4IoT.Hardware.OpenWeatherMapWeatherStation
{
    public class OWMWeatherStation : IWeatherStation
    {
        private readonly IHomeAutomationTimer _timer;
        private readonly ILogger _logger;
        private readonly Uri _weatherDataSourceUrl;
        private readonly WeatherStationTemperatureSensor _temperature;
        private readonly WeatherStationHumiditySensor _humidity;
        private readonly WeatherStationSituationSensor _situation;

        private string _previousResponse;
        private DateTime? _lastFetched;
        private DateTime? _lastFetchedDifferentResponse;
        private TimeSpan _sunrise;
        private TimeSpan _sunset;
        
        public OWMWeatherStation(DeviceId id, double lat, double lon, string appId, IHomeAutomationTimer timer, IHttpRequestController httpApi, ILogger logger)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (timer == null) throw new ArgumentNullException(nameof(timer));
            if (httpApi == null) throw new ArgumentNullException(nameof(httpApi));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _temperature = new WeatherStationTemperatureSensor(new ActuatorId("WeatherStation.Temperature"), httpApi, logger);
            TemperatureSensor = _temperature;

            _humidity = new WeatherStationHumiditySensor(new ActuatorId("WeatherStation.Humidity"), httpApi, logger);
            HumiditySensor = _humidity;

            _situation = new WeatherStationSituationSensor();
            SituationSensor = _situation;

            Id = id;
            _timer = timer;
            _logger = logger;
            _weatherDataSourceUrl = new Uri($"http://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&APPID={appId}&units=metric");

            LoadPersistedValues();

            Task.Factory.StartNew(async () => await FetchWeahterData(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            httpApi.HandleGet("weatherStation").Using(HandleApiGet);
            httpApi.HandlePost("weatherStation").Using(HandleApiPost);
        }

        public DeviceId Id { get; }

        // TODO: Move Daylight to other service because it is not part of weather state.
        public Daylight Daylight => new Daylight(_timer.CurrentTime, _sunrise, _sunset);

        public ITemperatureSensor TemperatureSensor { get; }
        public IHumiditySensor HumiditySensor { get; }
        public IWeatherSituationSensor SituationSensor { get; }

        public JsonObject ExportStatusToJsonObject()
        {
            var result = new JsonObject();
            result.SetNamedValue("uri", _weatherDataSourceUrl.ToString().ToJsonValue());

            result.SetNamedValue("situation", SituationSensor.GetSituation().ToJsonValue());
            result.SetNamedValue("temperature", TemperatureSensor.GetValue().ToJsonValue());
            result.SetNamedValue("humidity", HumiditySensor.GetValue().ToJsonValue());

            result.SetNamedValue("lastFetched", _lastFetched.ToJsonValue());
            result.SetNamedValue("lastFetchedDifferentResponse", _lastFetchedDifferentResponse.ToJsonValue());

            result.SetNamedValue("sunrise", _sunrise.ToJsonValue());
            result.SetNamedValue("sunset", _sunset.ToJsonValue());

            return result;
        }

        private async Task FetchWeahterData()
        {
            while (true)
            {
                try
                {
                    string response = await FetchWeatherData();

                    if (!string.Equals(response, _previousResponse))
                    {
                        PersistWeatherData(response);
                        ParseWeatherData(response);

                        _previousResponse = response;
                        _lastFetchedDifferentResponse = _timer.CurrentDateTime;
                    }

                    _lastFetched = _timer.CurrentDateTime;
                }
                catch (Exception exception)
                {
                    _logger.Warning(exception, "Could not fetch weather information");
                }
                finally
                {
                    await Task.Delay(5000);
                }
            }
        }

        private void PersistWeatherData(string weatherData)
        {
            string filename = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WeatherStationValues.json");
            File.WriteAllText(filename, weatherData);
        }

        private void ParseWeatherData(string weatherData)
        {
            var data = JsonObject.Parse(weatherData);

            var sys = data.GetNamedObject("sys");
            var main = data.GetNamedObject("main");
            var weather = data.GetNamedArray("weather");

            var sunriseValue = sys.GetNamedNumber("sunrise", 0);
            _sunrise = UnixTimeStampToDateTime(sunriseValue).TimeOfDay;

            var sunsetValue = sys.GetNamedNumber("sunset", 0);
            _sunset = UnixTimeStampToDateTime(sunsetValue).TimeOfDay;

            _situation.SetValue(weather.First().GetObject().GetNamedValue("id"));
            _temperature.SetValue(main.GetNamedNumber("temp", 0));
            _humidity.SetValue(main.GetNamedNumber("humidity", 0));
        }

        private async Task<string> FetchWeatherData()
        {
            using (var httpClient = new HttpClient())
            using (HttpResponseMessage result = await httpClient.GetAsync(_weatherDataSourceUrl))
            {
                return await result.Content.ReadAsStringAsync();
            }
        }

        private DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            var buffer = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            return buffer.AddSeconds(unixTimeStamp).ToLocalTime();
        }

        private void HandleApiPost(HttpContext context)
        {
            JsonObject requestData;
            if (JsonObject.TryParse(context.Request.Body, out requestData))
            {
                context.Response.StatusCode = HttpStatusCode.BadRequest;
                return;
            }

            _situation.SetValue(requestData.GetNamedValue("situation"));
            _temperature.SetValue((float)requestData.GetNamedNumber("temperature"));
            _humidity.SetValue((float)requestData.GetNamedNumber("humidity"));
            _sunrise = TimeSpan.Parse(requestData.GetNamedString("sunrise"));
            _sunset = TimeSpan.Parse(requestData.GetNamedString("sunset"));

            _lastFetched = DateTime.Now;
        }

        private void HandleApiGet(HttpContext httpContext)
        {
            httpContext.Response.Body = new JsonBody(ExportStatusToJsonObject());
        }

        private void LoadPersistedValues()
        {
            string filename = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WeatherStationValues.json");
            if (!File.Exists(filename))
            {
                return;
            }

            try
            {
                ParseWeatherData(File.ReadAllText(filename));
            }
            catch (Exception)
            {
                _logger.Warning("Unable to load persisted weather station values.");
                File.Delete(filename);
            }
        }
    }
}