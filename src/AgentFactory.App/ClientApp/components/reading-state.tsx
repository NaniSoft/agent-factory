import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { Skeleton } from '@nanisoft/prism-ui/components/skeleton';
import { Status } from '@nanisoft/prism-ui/components/status';

/** The page while its first read is still in flight. */
export function LoadingCard({ label }: { label: string }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Reading the factory</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="app-stack">
          <Status tone="info" label={label} />
          <Skeleton />
          <Skeleton />
          <Skeleton />
        </div>
      </CardContent>
    </Card>
  );
}

/** The page when the factory could not be reached, with the retry it offers. */
export function ErrorCard({
  title,
  message,
  onRetry,
}: {
  title: string;
  message: string;
  onRetry?: () => void;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="app-stack">
          <Alert variant="destructive">
            <AlertTitle>The factory did not answer</AlertTitle>
            <AlertDescription>{message}</AlertDescription>
          </Alert>
          {onRetry === undefined ? null : (
            <div>
              <Button type="button" variant="outline" onClick={onRetry}>
                Try again
              </Button>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
