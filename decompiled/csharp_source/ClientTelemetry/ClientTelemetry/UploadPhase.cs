namespace ClientTelemetry;

public enum UploadPhase
{
	Request,
	Put,
	MultipartPart,
	MultipartComplete,
	Complete,
	Local
}
